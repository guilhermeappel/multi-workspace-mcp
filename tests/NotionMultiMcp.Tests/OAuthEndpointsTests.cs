using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using NotionMultiMcp.Tests.Support;

namespace NotionMultiMcp.Tests;

public sealed class OAuthEndpointsTests : IDisposable
{
    // A client id is the signed registration itself, so a distinct client needs distinct registration data.
    private const string OtherClientRedirect = "https://claude.ai/api/mcp/auth_callback";

    private readonly McpAppFactory _factory = new();
    private readonly McpTestClient _client;

    public OAuthEndpointsTests()
    {
        _client = new McpTestClient(_factory);
    }

    public void Dispose() => _factory.Dispose();

    // ------------------------------------------------------------------ discovery

    [Fact]
    public async Task Protected_resource_metadata_points_at_the_mcp_url_and_this_server()
    {
        var response = await _client.Http.GetAsync("/.well-known/oauth-protected-resource");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await McpTestClient.ReadJsonAsync(response);
        Assert.Equal("https://mcp.test/mcp", (string)json["resource"]!);
        Assert.Equal("https://mcp.test", (string)json["authorization_servers"]![0]!);
    }

    [Fact]
    public async Task Protected_resource_metadata_is_also_served_under_the_mcp_path()
    {
        var response = await _client.Http.GetAsync("/.well-known/oauth-protected-resource/mcp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authorization_server_metadata_advertises_the_endpoints_and_pkce_s256()
    {
        var json = await McpTestClient.ReadJsonAsync(await _client.Http.GetAsync("/.well-known/oauth-authorization-server"));

        Assert.Equal("https://mcp.test", (string)json["issuer"]!);
        Assert.Equal("https://mcp.test/authorize", (string)json["authorization_endpoint"]!);
        Assert.Equal("https://mcp.test/token", (string)json["token_endpoint"]!);
        Assert.Equal("https://mcp.test/register", (string)json["registration_endpoint"]!);
        Assert.Equal("S256", (string)json["code_challenge_methods_supported"]![0]!);
        Assert.Equal("none", (string)json["token_endpoint_auth_methods_supported"]![0]!);
    }

    // ------------------------------------------------------------------ registration

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://claude.com/api/mcp/auth_callback")]
    [InlineData("http://localhost:5555/callback")]
    [InlineData("http://127.0.0.1:8123/cb")]
    public async Task Registration_accepts_claude_and_loopback_redirects(string redirectUri)
    {
        var clientId = await _client.RegisterAsync(redirectUri);

        Assert.False(string.IsNullOrEmpty(clientId));
    }

    [Theory]
    [InlineData("https://evil.example/callback")]
    [InlineData("http://evil.example/callback")]
    [InlineData("ftp://localhost/callback")]
    public async Task Registration_rejects_other_redirects(string redirectUri)
    {
        var body = new JsonObject { ["redirect_uris"] = new JsonArray(redirectUri) };

        var response = await _client.Http.PostAsync("/register", McpTestClient.Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_redirect_uri", (string)(await McpTestClient.ReadJsonAsync(response))["error"]!);
    }

    [Fact]
    public async Task Registration_requires_at_least_one_redirect_uri()
    {
        var response = await _client.Http.PostAsync("/register", McpTestClient.Json(new JsonObject()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registration_rejects_a_body_that_is_not_json()
    {
        var response = await _client.Http.PostAsync("/register", new StringContent("not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_client_metadata", (string)(await McpTestClient.ReadJsonAsync(response))["error"]!);
    }

    // ------------------------------------------------------------------ authorization page

    [Fact]
    public async Task Authorize_page_asks_for_the_password()
    {
        var clientId = await _client.RegisterAsync();
        var pkce = Pkce.New();
        var url = $"/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(McpTestClient.LoopbackRedirect)}" +
                  $"&code_challenge={pkce.Challenge}&code_challenge_method=S256&state=abc";

        var response = await _client.Http.GetAsync(url);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Connect to Notion", html);
        Assert.Contains("""name="password" """.TrimEnd(), html);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Authorize_page_escapes_hidden_fields()
    {
        var clientId = await _client.RegisterAsync();
        var url = $"/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(McpTestClient.LoopbackRedirect)}" +
                  $"&code_challenge={Pkce.New().Challenge}&code_challenge_method=S256&state={Uri.EscapeDataString("\"><script>alert(1)</script>")}";

        var html = await (await _client.Http.GetAsync(url)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task Authorize_rejects_an_unknown_client_with_an_error_page()
    {
        var response = await _client.Http.GetAsync(
            $"/authorize?client_id=nope&redirect_uri={Uri.EscapeDataString(McpTestClient.LoopbackRedirect)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Could not authorize", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Authorize_rejects_a_redirect_uri_that_was_not_registered()
    {
        var clientId = await _client.RegisterAsync("https://claude.ai/api/mcp/auth_callback");

        var response = await _client.AuthorizeAsync(clientId, Pkce.New(), redirectUri: "https://claude.com/api/mcp/auth_callback");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("redirect_uri is not registered", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Authorize_accepts_another_port_for_a_registered_loopback_redirect()
    {
        var clientId = await _client.RegisterAsync("http://localhost:5555/callback");

        var response = await _client.AuthorizeAsync(clientId, Pkce.New(), redirectUri: "http://localhost:6789/callback");

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(6789, response.Headers.Location!.Port);
    }

    [Fact]
    public async Task Authorize_rejects_a_different_path_on_the_loopback_redirect()
    {
        var clientId = await _client.RegisterAsync("http://localhost:5555/callback");

        var response = await _client.AuthorizeAsync(clientId, Pkce.New(), redirectUri: "http://localhost:5555/other");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Authorize_requires_pkce_and_reports_it_through_the_redirect()
    {
        var clientId = await _client.RegisterAsync();

        var response = await _client.AuthorizeAsync(clientId, new Pkce("unused", ""));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var query = McpTestClient.QueryOf(response);
        Assert.Equal("invalid_request", query["error"].ToString());
        Assert.Equal("state-123", query["state"].ToString());
    }

    [Fact]
    public async Task Correct_password_redirects_with_a_code_and_the_state()
    {
        var clientId = await _client.RegisterAsync();

        var response = await _client.AuthorizeAsync(clientId, Pkce.New());

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("localhost", response.Headers.Location!.Host);
        var query = McpTestClient.QueryOf(response);
        Assert.False(string.IsNullOrEmpty(query["code"].ToString()));
        Assert.Equal("state-123", query["state"].ToString());
    }

    [Fact]
    public async Task Wrong_password_is_rejected_without_a_code()
    {
        var clientId = await _client.RegisterAsync();

        var response = await _client.AuthorizeAsync(clientId, Pkce.New(), password: "wrong-password");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("Incorrect password.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_login_locks_after_ten_wrong_passwords_and_recovers_after_the_window()
    {
        var clientId = await _client.RegisterAsync();
        for (var i = 0; i < 10; i++)
        {
            await _client.AuthorizeAsync(clientId, Pkce.New(), password: $"wrong-{i}");
        }

        var locked = await _client.AuthorizeAsync(clientId, Pkce.New());
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);

        _factory.Time.Advance(TimeSpan.FromMinutes(16));
        var recovered = await _client.AuthorizeAsync(clientId, Pkce.New());
        Assert.Equal(HttpStatusCode.SeeOther, recovered.StatusCode);
    }

    // ------------------------------------------------------------------ token endpoint

    [Fact]
    public async Task Full_flow_issues_tokens_that_open_the_mcp_endpoint()
    {
        var (_, tokens) = await _client.SignInAsync();

        var response = await _client.RpcAsync(tokens.AccessToken, McpTestClient.Request(1, "ping"));

        Assert.NotNull(response["result"]);
    }

    [Fact]
    public async Task Token_response_has_the_bearer_shape_and_is_not_cacheable()
    {
        var clientId = await _client.RegisterAsync();
        var pkce = Pkce.New();
        var code = McpTestClient.QueryOf(await _client.AuthorizeAsync(clientId, pkce))["code"].ToString();

        var response = await _client.ExchangeCodeAsync(clientId, code, pkce.Verifier);
        var json = await McpTestClient.ReadJsonAsync(response);

        Assert.Equal("Bearer", (string)json["token_type"]!);
        Assert.Equal(3600, (int)json["expires_in"]!);
        Assert.NotNull(json["refresh_token"]);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Token_exchange_fails_with_a_wrong_pkce_verifier()
    {
        var clientId = await _client.RegisterAsync();
        var pkce = Pkce.New();
        var code = McpTestClient.QueryOf(await _client.AuthorizeAsync(clientId, pkce))["code"].ToString();

        var response = await _client.ExchangeCodeAsync(clientId, code, Pkce.New().Verifier);

        await AssertInvalidGrantAsync(response, "PKCE");
    }

    [Fact]
    public async Task An_authorization_code_can_be_used_only_once()
    {
        var clientId = await _client.RegisterAsync();
        var pkce = Pkce.New();
        var code = McpTestClient.QueryOf(await _client.AuthorizeAsync(clientId, pkce))["code"].ToString();

        var first = await _client.ExchangeCodeAsync(clientId, code, pkce.Verifier);
        var second = await _client.ExchangeCodeAsync(clientId, code, pkce.Verifier);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertInvalidGrantAsync(second, "already used");
    }

    [Fact]
    public async Task An_authorization_code_expires_after_five_minutes()
    {
        var clientId = await _client.RegisterAsync();
        var pkce = Pkce.New();
        var code = McpTestClient.QueryOf(await _client.AuthorizeAsync(clientId, pkce))["code"].ToString();

        _factory.Time.Advance(TimeSpan.FromMinutes(6));
        var response = await _client.ExchangeCodeAsync(clientId, code, pkce.Verifier);

        await AssertInvalidGrantAsync(response, "expired");
    }

    [Fact]
    public async Task Token_exchange_fails_when_the_redirect_uri_differs()
    {
        var clientId = await _client.RegisterAsync();
        var pkce = Pkce.New();
        var code = McpTestClient.QueryOf(await _client.AuthorizeAsync(clientId, pkce))["code"].ToString();

        var response = await _client.ExchangeCodeAsync(clientId, code, pkce.Verifier, redirectUri: "http://localhost:9999/callback");

        await AssertInvalidGrantAsync(response, "redirect_uri");
    }

    [Fact]
    public async Task Token_exchange_fails_for_a_different_client()
    {
        var clientId = await _client.RegisterAsync();
        var otherClientId = await _client.RegisterAsync(OtherClientRedirect);
        var pkce = Pkce.New();
        var code = McpTestClient.QueryOf(await _client.AuthorizeAsync(clientId, pkce))["code"].ToString();

        var response = await _client.ExchangeCodeAsync(otherClientId, code, pkce.Verifier);

        await AssertInvalidGrantAsync(response, "client_id");
    }

    [Fact]
    public async Task Token_exchange_fails_for_a_garbage_code()
    {
        var response = await _client.ExchangeCodeAsync("any", "garbage", Pkce.New().Verifier);

        await AssertInvalidGrantAsync(response, "Invalid or expired");
    }

    [Fact]
    public async Task Unsupported_grant_types_are_reported()
    {
        var response = await _client.TokenAsync(new Dictionary<string, string> { ["grant_type"] = "password" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsupported_grant_type", (string)(await McpTestClient.ReadJsonAsync(response))["error"]!);
    }

    [Fact]
    public async Task Token_endpoint_requires_a_form_body()
    {
        var response = await _client.Http.PostAsync("/token", McpTestClient.Json(new JsonObject()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (string)(await McpTestClient.ReadJsonAsync(response))["error"]!);
    }

    // ------------------------------------------------------------------ refresh and expiry

    [Fact]
    public async Task Refresh_token_issues_new_working_tokens()
    {
        var (clientId, tokens) = await _client.SignInAsync();

        var response = await _client.RefreshAsync(clientId, tokens.RefreshToken);
        var json = await McpTestClient.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rpc = await _client.RpcAsync((string)json["access_token"]!, McpTestClient.Request(1, "ping"));
        Assert.NotNull(rpc["result"]);
    }

    [Fact]
    public async Task Refresh_fails_for_a_different_client_or_a_garbage_token()
    {
        var (_, tokens) = await _client.SignInAsync();
        var otherClientId = await _client.RegisterAsync(OtherClientRedirect);

        await AssertInvalidGrantAsync(await _client.RefreshAsync(otherClientId, tokens.RefreshToken), "client_id");
        await AssertInvalidGrantAsync(await _client.RefreshAsync(otherClientId, "garbage"), "Invalid or expired");
    }

    [Fact]
    public async Task Refresh_token_is_rejected_after_ninety_days()
    {
        var (clientId, tokens) = await _client.SignInAsync();

        _factory.Time.Advance(TimeSpan.FromDays(91));
        var response = await _client.RefreshAsync(clientId, tokens.RefreshToken);

        await AssertInvalidGrantAsync(response, "expired");
    }

    [Fact]
    public async Task Access_token_stops_working_after_an_hour_but_refresh_still_does()
    {
        var (clientId, tokens) = await _client.SignInAsync();

        _factory.Time.Advance(TimeSpan.FromMinutes(61));
        var expired = await _client.PostMcpAsync(tokens.AccessToken, McpTestClient.Request(1, "ping"));
        var refreshed = await _client.RefreshAsync(clientId, tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }

    [Fact]
    public async Task Tokens_are_not_interchangeable_between_purposes()
    {
        var (_, tokens) = await _client.SignInAsync();

        var response = await _client.PostMcpAsync(tokens.RefreshToken, McpTestClient.Request(1, "ping"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task AssertInvalidGrantAsync(HttpResponseMessage response, string descriptionContains)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await McpTestClient.ReadJsonAsync(response);
        Assert.Equal("invalid_grant", (string)json["error"]!);
        Assert.Contains(descriptionContains, (string)json["error_description"]!);
    }

    // ------------------------------------------------------------------ resource indicator

    [Theory]
    [InlineData("https://mcp.test/mcp")]
    [InlineData("https://mcp.test/mcp/")]
    [InlineData("https://mcp.test")]
    public async Task Authorize_accepts_this_server_as_the_resource(string resource)
    {
        var clientId = await _client.RegisterAsync();

        var response = await _client.AuthorizeAsync(clientId, Pkce.New(), resource: resource);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(McpTestClient.QueryOf(response)["code"].ToString()));
    }

    [Theory]
    [InlineData("https://mcp.test.evil.example/mcp")]
    [InlineData("https://mcp.test/other")]
    [InlineData("https://other.example/mcp")]
    public async Task Authorize_rejects_any_other_resource(string resource)
    {
        var clientId = await _client.RegisterAsync();

        var response = await _client.AuthorizeAsync(clientId, Pkce.New(), resource: resource);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("invalid_target", McpTestClient.QueryOf(response)["error"].ToString());
    }

    // ------------------------------------------------------------------ request size

    [Fact]
    public async Task Registration_rejects_an_oversized_body()
    {
        var huge = new JsonObject { ["redirect_uris"] = new JsonArray(McpTestClient.LoopbackRedirect), ["client_name"] = new string('x', 40_000) };

        var response = await _client.Http.PostAsync("/register", McpTestClient.Json(huge));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Token_endpoint_rejects_an_oversized_body()
    {
        var response = await _client.TokenAsync(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = new string('x', 40_000) });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Authorization_form_rejects_an_oversized_body()
    {
        var clientId = await _client.RegisterAsync();

        var response = await _client.AuthorizeAsync(clientId, Pkce.New(), password: new string('x', 40_000));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // ------------------------------------------------------------------ logging of failures

    [Fact]
    public async Task A_wrong_password_is_logged_as_a_warning_without_the_password()
    {
        var clientId = await _client.RegisterAsync();

        await _client.AuthorizeAsync(clientId, Pkce.New(), password: "hunter2-not-the-password");

        var warning = Assert.Single(_factory.Logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("incorrect password", warning.Message);
        Assert.DoesNotContain(_factory.Logs.Entries, e => e.Message.Contains("hunter2") || e.Message.Contains(McpAppFactory.Password));
    }

    [Fact]
    public async Task The_lockout_is_logged()
    {
        var clientId = await _client.RegisterAsync();
        for (var i = 0; i < 10; i++)
        {
            await _client.AuthorizeAsync(clientId, Pkce.New(), password: $"wrong-{i}");
        }

        await _client.AuthorizeAsync(clientId, Pkce.New());

        Assert.Contains(_factory.Logs.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("locked"));
    }

    // ------------------------------------------------------------------ protocol details

    [Fact]
    public async Task Registration_omits_fields_it_has_no_value_for_and_is_not_cacheable()
    {
        var body = new JsonObject { ["redirect_uris"] = new JsonArray(McpTestClient.LoopbackRedirect) };

        var response = await _client.Http.PostAsync("/register", McpTestClient.Json(body));
        var json = (JsonObject)await McpTestClient.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(json.ContainsKey("client_name"));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.ToString());
    }

    [Fact]
    public async Task Registration_rejects_an_over_long_client_name_and_too_many_redirects()
    {
        var longName = new JsonObject { ["redirect_uris"] = new JsonArray(McpTestClient.LoopbackRedirect), ["client_name"] = new string('n', 201) };
        var manyRedirects = new JsonObject { ["redirect_uris"] = new JsonArray([.. Enumerable.Range(1, 11).Select(i => (JsonNode)$"http://localhost:{5000 + i}/cb")]) };

        foreach (var body in new[] { longName, manyRedirects })
        {
            var response = await _client.Http.PostAsync("/register", McpTestClient.Json(body));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_client_metadata", (string)(await McpTestClient.ReadJsonAsync(response))["error"]!);
        }
    }

    [Fact]
    public async Task The_token_response_is_marked_as_not_cacheable_in_both_ways()
    {
        var clientId = await _client.RegisterAsync();
        var pkce = Pkce.New();
        var code = McpTestClient.QueryOf(await _client.AuthorizeAsync(clientId, pkce))["code"].ToString();

        var response = await _client.ExchangeCodeAsync(clientId, code, pkce.Verifier);

        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.ToString());
    }

    [Fact]
    public async Task The_token_endpoint_requires_a_grant_type()
    {
        var response = await _client.TokenAsync(new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (string)(await McpTestClient.ReadJsonAsync(response))["error"]!);
    }

    [Fact]
    public async Task The_token_endpoint_rejects_a_resource_that_is_not_this_server()
    {
        var clientId = await _client.RegisterAsync();
        var pkce = Pkce.New();
        var code = McpTestClient.QueryOf(await _client.AuthorizeAsync(clientId, pkce))["code"].ToString();

        var response = await _client.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = pkce.Verifier,
            ["redirect_uri"] = McpTestClient.LoopbackRedirect,
            ["resource"] = "https://other.example/mcp",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_target", (string)(await McpTestClient.ReadJsonAsync(response))["error"]!);
    }

    [Theory]
    [InlineData("https://mcp.test/mcp")]
    [InlineData("https://mcp.test")]
    public async Task The_token_endpoint_accepts_this_server_as_the_resource(string resource)
    {
        var clientId = await _client.RegisterAsync();
        var pkce = Pkce.New();
        var code = McpTestClient.QueryOf(await _client.AuthorizeAsync(clientId, pkce))["code"].ToString();

        var response = await _client.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = pkce.Verifier,
            ["redirect_uri"] = McpTestClient.LoopbackRedirect,
            ["resource"] = resource,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authorize_reports_a_missing_response_type_as_an_invalid_request()
    {
        var clientId = await _client.RegisterAsync();
        var url = $"/authorize?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(McpTestClient.LoopbackRedirect)}" +
                  $"&code_challenge={Pkce.New().Challenge}&code_challenge_method=S256";

        var response = await _client.Http.GetAsync(url);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("invalid_request", McpTestClient.QueryOf(response)["error"].ToString());
    }

    [Fact]
    public async Task Authorize_reports_another_response_type_as_unsupported()
    {
        var clientId = await _client.RegisterAsync();
        var url = $"/authorize?response_type=token&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(McpTestClient.LoopbackRedirect)}" +
                  $"&code_challenge={Pkce.New().Challenge}&code_challenge_method=S256";

        var response = await _client.Http.GetAsync(url);

        Assert.Equal("unsupported_response_type", McpTestClient.QueryOf(response)["error"].ToString());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa+/=")]
    public async Task Authorize_rejects_a_code_challenge_that_is_not_a_base64url_sha256(string challenge)
    {
        var clientId = await _client.RegisterAsync();

        var response = await _client.AuthorizeAsync(clientId, new Pkce("unused", challenge));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("invalid_request", McpTestClient.QueryOf(response)["error"].ToString());
    }

    [Fact]
    public async Task A_verifier_with_characters_outside_the_allowed_set_fails_pkce()
    {
        var clientId = await _client.RegisterAsync();
        var verifier = new string('!', 50);
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var code = McpTestClient.QueryOf(await _client.AuthorizeAsync(clientId, new Pkce(verifier, challenge)))["code"].ToString();

        var response = await _client.ExchangeCodeAsync(clientId, code, verifier);

        await AssertInvalidGrantAsync(response, "PKCE");
    }

    [Fact]
    public async Task The_html_pages_carry_the_security_headers()
    {
        var clientId = await _client.RegisterAsync();
        var url = $"/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(McpTestClient.LoopbackRedirect)}" +
                  $"&code_challenge={Pkce.New().Challenge}&code_challenge_method=S256";

        var response = await _client.Http.GetAsync(url);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("max-age=", response.Headers.GetValues("Strict-Transport-Security").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("base-uri 'none'", csp);
        Assert.DoesNotContain("form-action", csp);
    }

    // ------------------------------------------------------------------ login throttling

    private async Task<HttpStatusCode> LoginAsync(string clientId, string password, string? from = null)
    {
        var response = await _client.AuthorizeAsync(clientId, Pkce.New(), password: password, clientAddress: from);
        return response.StatusCode;
    }

    [Fact]
    public async Task One_clients_wrong_passwords_do_not_lock_the_owner_out()
    {
        var clientId = await _client.RegisterAsync();
        for (var i = 0; i < 10; i++)
        {
            await LoginAsync(clientId, $"wrong-{i}", from: "198.51.100.7");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(clientId, McpAppFactory.Password, from: "198.51.100.7"));
        Assert.Equal(HttpStatusCode.SeeOther, await LoginAsync(clientId, McpAppFactory.Password, from: "203.0.113.50"));
    }

    [Fact]
    public async Task Many_clients_together_are_stopped_by_the_server_wide_limit()
    {
        var clientId = await _client.RegisterAsync();
        for (var address = 1; address <= 6; address++)
        {
            for (var i = 0; i < 10; i++)
            {
                await LoginAsync(clientId, $"wrong-{i}", from: $"198.51.100.{address}");
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(clientId, McpAppFactory.Password, from: "203.0.113.99"));
    }

    [Fact]
    public async Task A_correct_password_does_not_use_up_an_attempt()
    {
        var clientId = await _client.RegisterAsync();
        for (var i = 0; i < 9; i++)
        {
            await LoginAsync(clientId, $"wrong-{i}");
        }

        Assert.Equal(HttpStatusCode.SeeOther, await LoginAsync(clientId, McpAppFactory.Password));
        Assert.Equal(HttpStatusCode.SeeOther, await LoginAsync(clientId, McpAppFactory.Password));
        Assert.Equal(HttpStatusCode.Forbidden, await LoginAsync(clientId, "wrong-again"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(clientId, "wrong-last"));
    }

    [Fact]
    public async Task A_burst_of_concurrent_guesses_is_cut_off_at_the_limit()
    {
        var clientId = await _client.RegisterAsync();

        var statuses = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => LoginAsync(clientId, $"guess-{i}", from: "198.51.100.9")));

        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.Forbidden));
        Assert.Equal(30, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task A_locked_login_says_how_long_to_wait()
    {
        var clientId = await _client.RegisterAsync();
        for (var i = 0; i < 10; i++)
        {
            await LoginAsync(clientId, $"wrong-{i}");
        }

        _factory.Time.Advance(TimeSpan.FromMinutes(5));
        var response = await _client.AuthorizeAsync(clientId, Pkce.New());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("600", response.Headers.GetValues("Retry-After").Single());
        Assert.Contains("10 minute(s)", await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------ sessions

    [Fact]
    public async Task A_session_keeps_sliding_but_ends_after_the_absolute_limit()
    {
        var (clientId, tokens) = await _client.SignInAsync();
        var refreshToken = tokens.RefreshToken;

        // Day 60, 120 and 170: every refresh is valid and issues the next refresh token.
        foreach (var days in new[] { 60, 60, 50 })
        {
            _factory.Time.Advance(TimeSpan.FromDays(days));
            var refreshed = await _client.RefreshAsync(clientId, refreshToken);
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            refreshToken = (string)(await McpTestClient.ReadJsonAsync(refreshed))["refresh_token"]!;
        }

        // Day 181: the session started 180 days ago, so its last refresh token is no longer good.
        _factory.Time.Advance(TimeSpan.FromDays(11));
        await AssertInvalidGrantAsync(await _client.RefreshAsync(clientId, refreshToken), "expired");
    }

    [Fact]
    public async Task A_new_login_starts_a_new_session()
    {
        var (_, first) = await _client.SignInAsync();
        _factory.Time.Advance(TimeSpan.FromDays(150));

        var (clientId, second) = await _client.SignInAsync();
        _factory.Time.Advance(TimeSpan.FromDays(60));

        Assert.Equal(HttpStatusCode.OK, (await _client.RefreshAsync(clientId, second.RefreshToken)).StatusCode);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
    }

    // ------------------------------------------------------------------ consent and errors

    [Fact]
    public async Task The_login_page_warns_to_continue_only_from_your_own_claude()
    {
        var clientId = await _client.RegisterAsync();
        var url = $"/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(McpTestClient.LoopbackRedirect)}" +
                  $"&code_challenge={Pkce.New().Challenge}&code_challenge_method=S256";

        var html = await (await _client.Http.GetAsync(url)).Content.ReadAsStringAsync();

        Assert.Contains("Only continue if you just started connecting this server from your own Claude account", html);
    }

    [Fact]
    public async Task A_malformed_form_body_is_a_clean_400_instead_of_a_server_error()
    {
        using var content = new StringContent("--broken", Encoding.UTF8, "multipart/form-data");

        var response = await _client.Http.PostAsync("/authorize", content);
        var json = await McpTestClient.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (string)json["error"]!);
    }
}
