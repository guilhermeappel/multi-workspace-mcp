using System.Net;
using System.Text.Json.Nodes;
using NotionMultiMcp.Mcp;
using NotionMultiMcp.Tests.Support;

namespace NotionMultiMcp.Tests;

public sealed class McpEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly McpAppFactory _factory = new();
    private readonly McpTestClient _client;
    private string _token = "";

    public McpEndpointTests()
    {
        _client = new McpTestClient(_factory);
    }

    public async Task InitializeAsync() => _token = (await _client.SignInAsync()).Tokens.AccessToken;

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _factory.Dispose();

    // Bounded: the fake clock never advances by itself, so a call that wrongly waits to retry would hang.
    private Task<JsonNode> CallAsync(string tool, JsonObject arguments) =>
        _client.RpcAsync(_token, McpTestClient.ToolCall(1, tool, arguments)).WaitAsync(TimeSpan.FromSeconds(10));

    private static HttpResponseMessage NotionJson(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json) };

    // ------------------------------------------------------------------ transport and auth

    [Fact]
    public async Task Without_a_token_the_response_is_401_with_the_resource_metadata_hint()
    {
        var response = await _client.PostMcpAsync(null, McpTestClient.Request(1, "ping"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.Single();
        Assert.Equal("Bearer", challenge.Scheme);
        // No credentials were sent, so the challenge carries no error code (RFC 6750 section 3.1).
        Assert.Equal("resource_metadata=\"https://mcp.test/.well-known/oauth-protected-resource/mcp\"", challenge.Parameter);
        Assert.Equal("unauthorized", (string)(await McpTestClient.ReadJsonAsync(response))["error"]!);
    }

    [Fact]
    public async Task A_forged_token_is_rejected()
    {
        var response = await _client.PostMcpAsync("eyJmb28iOiJiYXIifQ.c2lnbmF0dXJl", McpTestClient.Request(1, "ping"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            "error=\"invalid_token\", resource_metadata=\"https://mcp.test/.well-known/oauth-protected-resource/mcp\"",
            response.Headers.WwwAuthenticate.Single().Parameter);
        Assert.Equal("invalid_token", (string)(await McpTestClient.ReadJsonAsync(response))["error"]!);
    }

    [Fact]
    public async Task Get_and_delete_are_not_allowed_because_there_is_no_sse_stream_or_session()
    {
        var get = await _client.Http.GetAsync("/mcp");
        var delete = await _client.Http.DeleteAsync("/mcp");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.Equal("POST", Assert.Single(get.Content.Headers.Allow));
        Assert.Equal("POST", Assert.Single(delete.Content.Headers.Allow));
    }

    [Fact]
    public async Task Health_endpoint_answers_ok_without_authentication()
    {
        var response = await _client.Http.GetAsync("/healthz");

        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_parse_error()
    {
        var response = await _client.PostMcpAsync(_token, "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32700, (int)(await McpTestClient.ReadJsonAsync(response))["error"]!["code"]!);
    }

    [Fact]
    public async Task A_notification_is_accepted_without_a_body()
    {
        var notification = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };

        var response = await _client.PostMcpAsync(_token, notification);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_batch_returns_an_array_with_one_answer_per_request()
    {
        var batch = new JsonArray(
            McpTestClient.Request(1, "ping"),
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
            McpTestClient.Request(2, "ping"));

        var response = await _client.RpcAsync(_token, batch);

        var answers = Assert.IsType<JsonArray>(response);
        Assert.Equal([1, 2], answers.Select(a => (int)a!["id"]!));
    }

    [Fact]
    public async Task An_unknown_method_is_a_method_not_found_error()
    {
        var response = await _client.RpcAsync(_token, McpTestClient.Request(7, "does/not/exist"));

        Assert.Equal(-32601, (int)response["error"]!["code"]!);
        Assert.Equal(7, (int)response["id"]!);
    }

    [Fact]
    public async Task A_non_object_message_is_an_invalid_request()
    {
        var response = await _client.RpcAsync(_token, new JsonArray(JsonValue.Create(5)));

        Assert.Equal(-32600, (int)response[0]!["error"]!["code"]!);
        Assert.Null(response[0]!["id"]);
    }

    // ------------------------------------------------------------------ protocol

    [Fact]
    public async Task Initialize_echoes_a_supported_protocol_version_and_describes_the_server()
    {
        var response = await _client.RpcAsync(_token,
            McpTestClient.Request(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" }));

        var result = response["result"]!;
        Assert.Equal("2025-03-26", (string)result["protocolVersion"]!);
        Assert.Equal("multi-workspace-mcp", (string)result["serverInfo"]!["name"]!);
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.Contains("personal", (string)result["instructions"]!);
        Assert.Contains("work", (string)result["instructions"]!);
    }

    [Fact]
    public async Task Initialize_falls_back_to_the_latest_version_for_an_unknown_one()
    {
        var response = await _client.RpcAsync(_token,
            McpTestClient.Request(1, "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" }));

        Assert.Equal("2025-11-25", (string)response["result"]!["protocolVersion"]!);
    }

    [Fact]
    public async Task Empty_resource_and_prompt_listings_are_served()
    {
        Assert.Empty((JsonArray)(await _client.RpcAsync(_token, McpTestClient.Request(1, "resources/list")))["result"]!["resources"]!);
        Assert.Empty((JsonArray)(await _client.RpcAsync(_token, McpTestClient.Request(2, "prompts/list")))["result"]!["prompts"]!);
    }

    // ------------------------------------------------------------------ tool definitions

    [Fact]
    public async Task Tools_list_exposes_every_tool()
    {
        var response = await _client.RpcAsync(_token, McpTestClient.Request(1, "tools/list"));

        var names = ((JsonArray)response["result"]!["tools"]!).Select(t => (string)t!["name"]!).ToArray();
        Assert.Equal(
            ["list_workspaces", "search", "fetch_page", "fetch_data_source", "query_data_source", "create_page", "append_content", "update_page"],
            names);
    }

    [Fact]
    public async Task Every_notion_tool_requires_the_workspace_argument_restricted_to_the_configured_names()
    {
        var response = await _client.RpcAsync(_token, McpTestClient.Request(1, "tools/list"));

        foreach (var tool in ((JsonArray)response["result"]!["tools"]!).Where(t => !string.Equals((string)t!["name"]!, "list_workspaces", StringComparison.Ordinal)))
        {
            var schema = tool!["inputSchema"]!;
            Assert.Contains("workspace", ((JsonArray)schema["required"]!).Select(r => (string)r!));
            var allowed = ((JsonArray)schema["properties"]!["workspace"]!["enum"]!).Select(e => (string)e!).ToArray();
            Assert.Equal(["personal", "work"], allowed);
        }
    }

    [Fact]
    public async Task List_workspaces_does_not_take_a_workspace_argument()
    {
        var response = await _client.RpcAsync(_token, McpTestClient.Request(1, "tools/list"));

        var tool = ((JsonArray)response["result"]!["tools"]!).Single(t => string.Equals((string)t!["name"]!, "list_workspaces", StringComparison.Ordinal))!;
        Assert.Empty((JsonObject)tool["inputSchema"]!["properties"]!);
    }

    [Fact]
    public async Task Read_only_tools_are_annotated_as_such()
    {
        var response = await _client.RpcAsync(_token, McpTestClient.Request(1, "tools/list"));

        var tools = ((JsonArray)response["result"]!["tools"]!).ToDictionary(t => (string)t!["name"]!, t => t!["annotations"]!);
        var readOnly = tools.Where(t => (bool)t.Value["readOnlyHint"]!).Select(t => t.Key).ToArray();
        Assert.Equal(["list_workspaces", "search", "fetch_page", "fetch_data_source", "query_data_source"], readOnly);

        // Reads are idempotent and never destructive; creating and appending add data without overwriting.
        Assert.All(readOnly, name => Assert.True((bool)tools[name]["idempotentHint"]!));
        Assert.All(tools.Values, annotations => Assert.True((bool)annotations["openWorldHint"]!));
        Assert.False((bool)tools["create_page"]["destructiveHint"]!);
        Assert.False((bool)tools["append_content"]["destructiveHint"]!);
        Assert.False((bool)tools["create_page"]["idempotentHint"]!);

        // update_page overwrites values and can move a page to the trash, but repeating it changes nothing more.
        Assert.True((bool)tools["update_page"]["destructiveHint"]!);
        Assert.True((bool)tools["update_page"]["idempotentHint"]!);
    }

    // ------------------------------------------------------------------ tool calls

    [Fact]
    public async Task Calling_an_unknown_tool_is_an_invalid_params_error()
    {
        var response = await _client.RpcAsync(_token, McpTestClient.ToolCall(1, "nope", []));

        Assert.Equal(-32602, (int)response["error"]!["code"]!);
    }

    [Fact]
    public async Task A_missing_workspace_is_a_tool_error_listing_the_options()
    {
        var response = await CallAsync("search", []);

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("personal, work", McpTestClient.ToolText(response));
    }

    [Fact]
    public async Task An_unknown_workspace_is_a_tool_error()
    {
        var response = await CallAsync("search", new JsonObject { ["workspace"] = "clients" });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("Unknown workspace 'clients'", McpTestClient.ToolText(response));
    }

    [Fact]
    public async Task Each_workspace_uses_its_own_notion_token()
    {
        _factory.Notion.Respond(_ => NotionJson(HttpStatusCode.OK, """{"results":[]}"""));

        await CallAsync("search", new JsonObject { ["workspace"] = "work", ["query"] = "plan" });
        await CallAsync("search", new JsonObject { ["workspace"] = "personal", ["query"] = "trip" });

        Assert.Equal(
            [$"Bearer {McpAppFactory.WorkToken}", $"Bearer {McpAppFactory.PersonalToken}"],
            _factory.Notion.Requests.Select(r => r.Authorization));
        Assert.All(_factory.Notion.Requests, r => Assert.Equal("search", r.Path));
        Assert.All(_factory.Notion.Requests, r => Assert.Equal("2026-03-11", r.NotionVersion));
    }

    [Fact]
    public async Task Search_formats_pages_and_data_sources()
    {
        _factory.Notion.Respond(_ => NotionJson(HttpStatusCode.OK, """
            {"results":[
              {"object":"page","id":"p1","url":"https://notion.so/p1","last_edited_time":"2026-09-30T10:00:00Z",
               "properties":{"Name":{"type":"title","title":[{"plain_text":"Onboarding"}]}}},
              {"object":"data_source","id":"d1","url":"https://notion.so/d1","last_edited_time":"2026-09-29T10:00:00Z",
               "title":[{"plain_text":"Tasks"}]}
            ],"has_more":true,"next_cursor":"cursor-2"}
            """));

        var text = McpTestClient.ToolText(await CallAsync("search", new JsonObject { ["workspace"] = "work", ["query"] = "x" }));

        Assert.Contains("- [page] Onboarding | id: p1", text);
        Assert.Contains("- [data_source] Tasks | id: d1", text);
        Assert.Contains("next_cursor: cursor-2", text);
    }

    [Fact]
    public async Task Search_sends_the_query_the_filter_and_the_page_size()
    {
        _factory.Notion.Respond(_ => NotionJson(HttpStatusCode.OK, """{"results":[]}"""));

        await CallAsync("search", new JsonObject { ["workspace"] = "work", ["query"] = "plan", ["type"] = "page", ["limit"] = 5 });

        var body = JsonNode.Parse(_factory.Notion.Requests.Single().Body!)!;
        Assert.Equal("plan", (string)body["query"]!);
        Assert.Equal(5, (int)body["page_size"]!);
        Assert.Equal("page", (string)body["filter"]!["value"]!);
    }

    [Fact]
    public async Task A_notion_error_becomes_a_tool_error_with_a_hint()
    {
        _factory.Notion.Respond(_ => NotionJson(HttpStatusCode.NotFound, """{"code":"object_not_found","message":"Could not find page"}"""));

        var response = await CallAsync("fetch_page", new JsonObject { ["workspace"] = "work", ["page_id"] = "1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f" });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("object_not_found", McpTestClient.ToolText(response));
        Assert.Contains("Connections", McpTestClient.ToolText(response));
    }

    [Fact]
    public async Task Fetch_page_needs_a_page_id()
    {
        var response = await CallAsync("fetch_page", new JsonObject { ["workspace"] = "work" });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("Missing required argument 'page_id'", McpTestClient.ToolText(response));
    }

    [Fact]
    public async Task List_workspaces_reports_each_workspace_with_its_real_notion_name()
    {
        _factory.Notion.Respond(request => NotionJson(HttpStatusCode.OK, request.Headers.Authorization!.Parameter == McpAppFactory.WorkToken
            ? """{"name":"Bot A","bot":{"workspace_name":"Acme Inc"}}"""
            : """{"name":"Bot B","bot":{"workspace_name":"Home"}}"""));

        var text = McpTestClient.ToolText(await CallAsync("list_workspaces", []));

        Assert.Contains("- work: Notion workspace \"Acme Inc\"", text);
        Assert.Contains("- personal: Notion workspace \"Home\"", text);
    }

    [Fact]
    public async Task List_workspaces_keeps_going_when_one_token_is_bad()
    {
        _factory.Notion.Respond(request => request.Headers.Authorization!.Parameter == McpAppFactory.WorkToken
            ? NotionJson(HttpStatusCode.Unauthorized, """{"code":"unauthorized","message":"API token is invalid."}""")
            : NotionJson(HttpStatusCode.OK, """{"name":"Bot B","bot":{"workspace_name":"Home"}}"""));

        var response = await CallAsync("list_workspaces", []);

        Assert.False(McpTestClient.ToolIsError(response));
        Assert.Contains("- work: ERROR", McpTestClient.ToolText(response));
        Assert.Contains("- personal: Notion workspace \"Home\"", McpTestClient.ToolText(response));
    }

    [Fact]
    public async Task Create_page_under_a_parent_page_posts_title_and_markdown_blocks()
    {
        _factory.Notion.Respond(_ => NotionJson(HttpStatusCode.OK, """
            {"id":"new-page","url":"https://notion.so/new-page",
             "properties":{"title":{"type":"title","title":[{"plain_text":"Trip"}]}}}
            """));

        var response = await CallAsync("create_page", new JsonObject
        {
            ["workspace"] = "personal",
            ["parent_page_id"] = "1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f",
            ["title"] = "Trip",
            ["content_markdown"] = "# Packing\n\n- [ ] Passport",
        });

        Assert.False(McpTestClient.ToolIsError(response));
        Assert.Contains("id: new-page", McpTestClient.ToolText(response));
        var request = _factory.Notion.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("pages", request.Path);
        Assert.Equal($"Bearer {McpAppFactory.PersonalToken}", request.Authorization);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f", (string)body["parent"]!["page_id"]!);
        Assert.Equal(["heading_1", "to_do"], ((JsonArray)body["children"]!).Select(b => (string)b!["type"]!));
    }

    [Fact]
    public async Task Create_page_requires_exactly_one_parent()
    {
        var neither = await CallAsync("create_page", new JsonObject { ["workspace"] = "work", ["title"] = "x" });
        var both = await CallAsync("create_page", new JsonObject
        {
            ["workspace"] = "work",
            ["parent_page_id"] = "a",
            ["parent_data_source_id"] = "b",
        });

        Assert.True(McpTestClient.ToolIsError(neither));
        Assert.True(McpTestClient.ToolIsError(both));
        Assert.Contains("exactly one of parent_page_id or parent_data_source_id", McpTestClient.ToolText(both));
        Assert.Empty(_factory.Notion.Requests);
    }

    [Fact]
    public async Task Update_page_needs_something_to_update()
    {
        var response = await CallAsync("update_page", new JsonObject { ["workspace"] = "work", ["page_id"] = "1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f" });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("Nothing to update", McpTestClient.ToolText(response));
    }

    [Fact]
    public async Task Update_page_can_move_a_page_to_the_trash()
    {
        _factory.Notion.Respond(_ => NotionJson(HttpStatusCode.OK, """
            {"id":"p1","url":"https://notion.so/p1","properties":{"Name":{"type":"title","title":[{"plain_text":"Old"}]}}}
            """));

        var response = await CallAsync("update_page", new JsonObject
        {
            ["workspace"] = "work",
            ["page_id"] = "1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f",
            ["in_trash"] = true,
        });

        Assert.Contains("(moved to trash)", McpTestClient.ToolText(response));
        var request = _factory.Notion.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.True((bool)JsonNode.Parse(request.Body!)!["in_trash"]!);
    }

    [Fact]
    public async Task Arguments_sent_as_strings_are_tolerated()
    {
        _factory.Notion.Respond(_ => NotionJson(HttpStatusCode.OK, """{"results":[]}"""));

        await CallAsync("search", new JsonObject { ["workspace"] = "work", ["limit"] = "7" });

        Assert.Equal(7, (int)JsonNode.Parse(_factory.Notion.Requests.Single().Body!)!["page_size"]!);
    }

    [Fact]
    public async Task A_rejected_request_is_logged_without_the_token()
    {
        await _client.PostMcpAsync("forged.token-value", McpTestClient.Request(1, "ping"));

        Assert.Contains(_factory.Logs.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("/mcp"));
        Assert.DoesNotContain(_factory.Logs.Entries, e => e.Message.Contains("forged.token-value") || e.Message.Contains(_token));
    }

    [Fact]
    public async Task Searches_and_queries_use_the_read_only_post_so_they_survive_a_transient_failure()
    {
        _factory.Notion.Respond(
            _ => NotionJson(HttpStatusCode.BadGateway, "{}"),
            _ => NotionJson(HttpStatusCode.OK, """{"results":[]}"""));
        var call = CallAsync("search", new JsonObject { ["workspace"] = "work" });

        while (!call.IsCompleted)
        {
            _factory.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAny(call, Task.Delay(1));
        }

        Assert.False(McpTestClient.ToolIsError(await call));
        Assert.Equal(2, _factory.Notion.Requests.Count);
    }

    [Fact]
    public async Task A_failed_create_page_is_reported_once_and_never_repeated()
    {
        _factory.Notion.Respond(
            _ => NotionJson(HttpStatusCode.BadGateway, """{"code":"internal_server_error","message":"boom"}"""),
            _ => NotionJson(HttpStatusCode.OK, """{"id":"dup","url":"u","properties":{}}"""));

        var response = await CallAsync("create_page", new JsonObject
        {
            ["workspace"] = "work",
            ["parent_page_id"] = "1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f",
            ["title"] = "Once",
        });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Single(_factory.Notion.Requests);
    }

    // ------------------------------------------------------------------ origin and protocol version

    private Task<HttpResponseMessage> PingWithHeaderAsync(string? token, string name, string value) =>
        _client.PostMcpAsync(token, McpTestClient.Request(1, "ping"), new Dictionary<string, string> { [name] = value });

    [Theory]
    [InlineData("https://claude.ai")]
    [InlineData("https://claude.com")]
    [InlineData("https://mcp.test")]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:6274")]
    public async Task Requests_from_known_origins_are_served(string origin)
    {
        var response = await PingWithHeaderAsync(_token, "Origin", origin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://claude.ai")]
    [InlineData("https://claude.ai.evil.example")]
    [InlineData("https://user@claude.ai")]
    [InlineData("null")]
    [InlineData("not a url")]
    public async Task Requests_from_other_origins_are_refused(string origin)
    {
        var response = await PingWithHeaderAsync(_token, "Origin", origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(-32600, (int)(await McpTestClient.ReadJsonAsync(response))["error"]!["code"]!);
    }

    [Fact]
    public async Task The_origin_is_checked_before_the_token()
    {
        var response = await PingWithHeaderAsync(null, "Origin", "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_refused_origin_never_reaches_a_tool()
    {
        var call = McpTestClient.ToolCall(1, "search", new JsonObject { ["workspace"] = "work" });

        var response = await _client.PostMcpAsync(_token, call, new Dictionary<string, string> { ["Origin"] = "https://evil.example" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Notion.Requests);
    }

    [Fact]
    public async Task Extra_allowed_origins_can_be_added_by_configuration()
    {
        using var factory = new McpAppFactory(new Dictionary<string, string?> { ["Server:ExtraAllowedOrigins:0"] = "https://inspector.example" });
        var client = new McpTestClient(factory);
        var token = (await client.SignInAsync()).Tokens.AccessToken;
        var headers = new Dictionary<string, string> { ["Origin"] = "https://inspector.example" };

        var response = await client.PostMcpAsync(token, McpTestClient.Request(1, "ping"), headers);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    [InlineData("2025-03-26")]
    [InlineData("2024-11-05")]
    public async Task Supported_protocol_version_headers_are_accepted(string version)
    {
        var response = await PingWithHeaderAsync(_token, "MCP-Protocol-Version", version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("1999-01-01")]
    [InlineData("latest")]
    public async Task An_unsupported_protocol_version_header_is_a_bad_request(string version)
    {
        var response = await PingWithHeaderAsync(_token, "MCP-Protocol-Version", version);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("MCP-Protocol-Version", (string)(await McpTestClient.ReadJsonAsync(response))["error"]!["message"]!);
    }

    [Fact]
    public async Task A_missing_protocol_version_header_is_accepted()
    {
        var response = await _client.PostMcpAsync(_token, McpTestClient.Request(1, "ping"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_token_gets_the_invalid_token_challenge()
    {
        _factory.Time.Advance(TimeSpan.FromMinutes(61));

        var response = await _client.PostMcpAsync(_token, McpTestClient.Request(1, "ping"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.StartsWith("error=\"invalid_token\"", response.Headers.WwwAuthenticate.Single().Parameter);
    }

    // ------------------------------------------------------------------ JSON-RPC validation

    [Fact]
    public async Task An_empty_batch_is_a_single_invalid_request_error()
    {
        var response = await _client.PostMcpAsync(_token, "[]");
        var json = await McpTestClient.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32600, (int)json["error"]!["code"]!);
        Assert.Null(json["id"]);
    }

    [Fact]
    public async Task A_batch_over_the_limit_is_a_single_invalid_request_error()
    {
        var response = await _client.PostMcpAsync(_token, PingBatch(McpEndpoint.MaxBatchItems + 1));
        var json = await McpTestClient.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32600, (int)json["error"]!["code"]!);
        Assert.Null(json["id"]);
    }

    [Fact]
    public async Task A_batch_at_the_limit_is_still_answered()
    {
        var response = await _client.PostMcpAsync(_token, PingBatch(McpEndpoint.MaxBatchItems));
        var json = await McpTestClient.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(McpEndpoint.MaxBatchItems, json.AsArray().Count);
    }

    private static string PingBatch(int size) =>
        "[" + string.Join(",", Enumerable.Range(1, size).Select(id => $$"""{"jsonrpc":"2.0","id":{{id}},"method":"ping"}""")) + "]";

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"jsonrpc":"2.0"}""")]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"ping"}""")]
    [InlineData("""{"id":1,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":5}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":""}""")]
    [InlineData("""{"jsonrpc":"2.0","id":{"x":1},"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":null,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":true,"method":"ping"}""")]
    [InlineData("""5""")]
    [InlineData("\"text\"")]
    [InlineData("""null""")]
    public async Task A_malformed_message_is_an_invalid_request(string body)
    {
        var response = await _client.PostMcpAsync(_token, body);
        var json = await McpTestClient.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32600, (int)json["error"]!["code"]!);
    }

    [Fact]
    public async Task An_invalid_message_echoes_its_id_when_the_id_is_valid()
    {
        var response = await _client.PostMcpAsync(_token, """{"jsonrpc":"1.0","id":"abc","method":"ping"}""");
        var json = await McpTestClient.ReadJsonAsync(response);

        Assert.Equal("abc", (string)json["id"]!);
    }

    [Fact]
    public async Task A_batch_answers_each_item_and_reports_the_invalid_ones_in_place()
    {
        var batch = """[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","id":2},{"jsonrpc":"2.0","id":3,"method":"ping"}]""";

        var response = await _client.PostMcpAsync(_token, batch);
        var answers = (JsonArray)await McpTestClient.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, answers.Count);
        Assert.NotNull(answers[0]!["result"]);
        Assert.Equal(-32600, (int)answers[1]!["error"]!["code"]!);
        Assert.Equal(2, (int)answers[1]!["id"]!);
        Assert.NotNull(answers[2]!["result"]);
    }

    [Fact]
    public async Task A_response_sent_by_the_client_is_accepted_without_an_answer()
    {
        var response = await _client.PostMcpAsync(_token, """{"jsonrpc":"2.0","id":7,"result":{}}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task String_and_numeric_ids_are_echoed_back_unchanged()
    {
        var text = await McpTestClient.ReadJsonAsync(await _client.PostMcpAsync(_token, """{"jsonrpc":"2.0","id":"req-1","method":"ping"}"""));
        var number = await McpTestClient.ReadJsonAsync(await _client.PostMcpAsync(_token, """{"jsonrpc":"2.0","id":42,"method":"ping"}"""));

        Assert.Equal("req-1", (string)text["id"]!);
        Assert.Equal(42, (int)number["id"]!);
    }

    [Fact]
    public async Task Ping_answers_with_an_empty_object()
    {
        var response = await _client.RpcAsync(_token, McpTestClient.Request(1, "ping"));

        var result = Assert.IsType<JsonObject>(response["result"]);
        Assert.Empty(result);
    }

    [Fact]
    public async Task The_health_endpoints_answer_head_requests_and_are_not_cacheable()
    {
        var head = await _client.Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/healthz"));
        var get = await _client.Http.GetAsync("/healthz");

        // The in-memory test server does not strip the body of a HEAD response the way Kestrel does.
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Contains("no-store", head.Headers.CacheControl!.ToString());
        Assert.Contains("no-store", get.Headers.CacheControl!.ToString());
    }
}
