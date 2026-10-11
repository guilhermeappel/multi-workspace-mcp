using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace NotionMultiMcp.Tests.Support;

internal sealed record Pkce(string Verifier, string Challenge)
{
    public static Pkce New()
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new Pkce(verifier, challenge);
    }
}

internal sealed record TokenPair(string AccessToken, string RefreshToken);

/// <summary>Plays the role of Claude: registers, authorizes, exchanges tokens and speaks JSON-RPC.</summary>
internal sealed class McpTestClient(McpAppFactory factory)
{
    public const string LoopbackRedirect = "http://localhost:5555/callback";

    public HttpClient Http { get; } = factory.CreateNoRedirectClient();

    public async Task<string> RegisterAsync(string redirectUri = LoopbackRedirect)
    {
        var body = new JsonObject { ["redirect_uris"] = new JsonArray(redirectUri), ["client_name"] = "test" };
        var response = await Http.PostAsync("/register", Json(body));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (string)(await ReadJsonAsync(response))["client_id"]!;
    }

    public Task<HttpResponseMessage> AuthorizeAsync(
        string clientId,
        Pkce pkce,
        string password = McpAppFactory.Password,
        string redirectUri = LoopbackRedirect,
        string? state = "state-123",
        string? resource = null,
        string? clientAddress = null)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["password"] = password,
        };
        if (state is not null)
        {
            form["state"] = state;
        }

        if (resource is not null)
        {
            form["resource"] = resource;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/authorize") { Content = new FormUrlEncodedContent(form) };
        if (clientAddress is not null)
        {
            // The app trusts X-Forwarded-For (it sits behind the Container Apps ingress), so this plays another client.
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientAddress);
        }

        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> TokenAsync(IDictionary<string, string> form) =>
        Http.PostAsync("/token", new FormUrlEncodedContent(form));

    public Task<HttpResponseMessage> ExchangeCodeAsync(string clientId, string code, string verifier, string redirectUri = LoopbackRedirect) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
        });

    public Task<HttpResponseMessage> RefreshAsync(string clientId, string refreshToken) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        });

    /// <summary>Runs registration, authorization and the code exchange; returns the issued tokens.</summary>
    public async Task<(string ClientId, TokenPair Tokens)> SignInAsync()
    {
        var clientId = await RegisterAsync();
        var pkce = Pkce.New();
        var authorized = await AuthorizeAsync(clientId, pkce);
        Assert.Equal(HttpStatusCode.SeeOther, authorized.StatusCode);

        var code = QueryOf(authorized)["code"].ToString();
        var exchanged = await ExchangeCodeAsync(clientId, code, pkce.Verifier);
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);

        var json = await ReadJsonAsync(exchanged);
        return (clientId, new TokenPair((string)json["access_token"]!, (string)json["refresh_token"]!));
    }

    public async Task<JsonNode> RpcAsync(string accessToken, JsonNode message)
    {
        var response = await PostMcpAsync(accessToken, message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    public Task<HttpResponseMessage> PostMcpAsync(string? accessToken, JsonNode message, IReadOnlyDictionary<string, string>? headers = null) =>
        PostMcpAsync(accessToken, message.ToJsonString(), headers);

    public async Task<HttpResponseMessage> PostMcpAsync(string? accessToken, string rawBody, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
        };
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return await Http.SendAsync(request);
    }

    public static JsonObject Request(int id, string method, JsonNode? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return message;
    }

    public static JsonObject ToolCall(int id, string name, JsonObject arguments) =>
        Request(id, "tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments });

    public static StringContent Json(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    /// <summary>Query string of the redirect a response points to.</summary>
    public static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> QueryOf(HttpResponseMessage response) =>
        QueryHelpers.ParseQuery(response.Headers.Location!.Query);

    /// <summary>The text of the first content item of a tools/call result.</summary>
    public static string ToolText(JsonNode rpcResponse) => (string)rpcResponse["result"]!["content"]![0]!["text"]!;

    public static bool ToolIsError(JsonNode rpcResponse) => (bool)rpcResponse["result"]!["isError"]!;
}
