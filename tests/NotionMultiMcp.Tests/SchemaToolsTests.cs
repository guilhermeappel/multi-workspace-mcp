using System.Net;
using System.Text.Json.Nodes;
using NotionMultiMcp.Tests.Support;

namespace NotionMultiMcp.Tests;

/// <summary>Icons and covers on pages, update_data_source, create_database and the richer fetch_data_source, end to end.</summary>
public sealed class SchemaToolsTests : IAsyncLifetime, IDisposable
{
    private const string PageId = "1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f";
    private const string ParentId = "cccccccccccccccccccccccccccccccc";
    private const string DataSourceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DatabaseId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Work = "work";

    private readonly McpAppFactory _factory = new();
    private readonly McpTestClient _client;
    private string _token = "";

    public SchemaToolsTests()
    {
        _client = new McpTestClient(_factory);
    }

    public async Task InitializeAsync() => _token = (await _client.SignInAsync()).Tokens.AccessToken;

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _factory.Dispose();

    private static JsonObject Raw(string json) => JsonNode.Parse(json)!.AsObject();

    private static HttpResponseMessage Ok(JsonNode json) => StubHttpHandler.Ok(json.ToJsonString());

    private void Route(Func<string, string, HttpResponseMessage?> routes) =>
        _factory.Notion.Respond(request => routes(request.Method.Method, StubHttpHandler.PathOf(request)) ?? StubHttpHandler.NotFound());

    private async Task<JsonNode> CallAsync(string tool, JsonObject arguments)
    {
        arguments["workspace"] = Work;
        return await _client.RpcAsync(_token, McpTestClient.ToolCall(1, tool, arguments)).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static string TextOf(JsonNode response) => McpTestClient.ToolText(response).ReplaceLineEndings("\n");

    private JsonNode BodyOf(int index) => JsonNode.Parse(_factory.Notion.Requests[index].Body!)!;

    private static JsonObject Page(string id) => Raw(
        "{\"id\":\"" + id + "\",\"url\":\"https://notion.so/" + id + "\",\"properties\":{\"Name\":{\"type\":\"title\",\"title\":[{\"plain_text\":\"Row\"}]}}}");

    private const string Columns = """
        {"Name":{"id":"title","type":"title","title":{}},
         "Tickets":{"id":"t","type":"url","url":{}},
         "Status":{"id":"s","type":"select","select":{"options":[
            {"id":"id-a","name":"Todo","color":"red"},
            {"id":"id-b","name":"Reviewed","color":"green"}]}}}
        """;

    private static JsonObject DataSource(string title, string columns = Columns) => Raw(
        "{\"id\":\"" + DataSourceId + "\",\"title\":[{\"plain_text\":\"" + title + "\"}],\"parent\":{\"database_id\":\"" + DatabaseId + "\"},\"properties\":" + columns + "}");

    // ------------------------------------------------------------------ icon and cover on pages

    [Fact]
    public async Task Create_page_sends_the_emoji_icon_and_the_cover()
    {
        Route((method, path) => (method, path) == ("POST", "pages") ? Ok(Page(PageId)) : null);

        var response = await CallAsync("create_page", new JsonObject
        {
            ["parent_page_id"] = ParentId,
            ["title"] = "Row",
            ["icon"] = "⭐",
            ["cover"] = "https://example.com/c.jpg",
        });

        Assert.False(McpTestClient.ToolIsError(response));
        var body = BodyOf(0);
        Assert.Equal("emoji", (string)body["icon"]!["type"]!);
        Assert.Equal("⭐", (string)body["icon"]!["emoji"]!);
        Assert.Equal("https://example.com/c.jpg", (string)body["cover"]!["external"]!["url"]!);
    }

    [Fact]
    public async Task Create_page_with_a_url_icon_sends_an_external_icon_and_none_sends_nothing()
    {
        Route((method, path) => (method, path) == ("POST", "pages") ? Ok(Page(PageId)) : null);

        await CallAsync("create_page", new JsonObject { ["parent_page_id"] = ParentId, ["icon"] = "https://example.com/i.png", ["cover"] = "none" });
        var body = BodyOf(0);
        Assert.Equal("https://example.com/i.png", (string)body["icon"]!["external"]!["url"]!);
        Assert.False(((JsonObject)body).ContainsKey("cover"));

        _factory.Notion.Respond(_ => Ok(Page(PageId)));
        await CallAsync("create_page", new JsonObject { ["parent_page_id"] = ParentId, ["icon"] = "none" });
        Assert.False(((JsonObject)BodyOf(0)).ContainsKey("icon"));
    }

    [Fact]
    public async Task Update_page_can_change_only_the_icon_without_reading_the_page()
    {
        Route((method, path) => (method, path) == ("PATCH", $"pages/{PageId}") ? Ok(Page(PageId)) : null);

        var response = await CallAsync("update_page", new JsonObject { ["page_id"] = PageId, ["icon"] = "🚚" });

        Assert.False(McpTestClient.ToolIsError(response));
        var request = Assert.Single(_factory.Notion.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal(["icon"], BodyOf(0).AsObject().Select(p => p.Key));
        Assert.Equal("emoji", (string)BodyOf(0)["icon"]!["type"]!);
        Assert.Equal("🚚", (string)BodyOf(0)["icon"]!["emoji"]!);
    }

    [Fact]
    public async Task Update_page_with_none_removes_the_icon_and_the_cover()
    {
        Route((method, path) => (method, path) == ("PATCH", $"pages/{PageId}") ? Ok(Page(PageId)) : null);

        await CallAsync("update_page", new JsonObject { ["page_id"] = PageId, ["icon"] = "none", ["cover"] = "none" });

        Assert.Equal("""{"icon":null,"cover":null}""", BodyOf(0).ToJsonString());
    }

    [Fact]
    public async Task Update_page_with_an_icon_next_to_properties_sends_both()
    {
        Route((method, path) => path switch
        {
            _ when (method, path) == ("GET", $"pages/{PageId}") => Ok(Page(PageId)),
            _ when (method, path) == ("PATCH", $"pages/{PageId}") => Ok(Page(PageId)),
            _ => null,
        });

        await CallAsync("update_page", new JsonObject { ["page_id"] = PageId, ["title"] = "New", ["icon"] = "📦" });

        var body = BodyOf(1);
        Assert.Equal("📦", (string)body["icon"]!["emoji"]!);
        Assert.NotNull(body["properties"]);
    }

    [Fact]
    public async Task A_bad_icon_is_an_error_and_nothing_is_sent()
    {
        var response = await CallAsync("update_page", new JsonObject { ["page_id"] = PageId, ["icon"] = "star" });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Empty(_factory.Notion.Requests);
    }

    // ------------------------------------------------------------------ update_data_source

    [Fact]
    public async Task Update_data_source_patches_the_schema_on_the_data_source_and_the_title_and_icon_on_the_database()
    {
        var calls = 0;
        _factory.Notion.Respond(request =>
        {
            var path = StubHttpHandler.PathOf(request);
            if (request.Method == HttpMethod.Get && path == $"data_sources/{DataSourceId}")
            {
                // The first read is the schema before, the last one the schema after.
                return Ok(++calls == 1
                    ? DataSource("Career Track")
                    : DataSource("Deliveries", Columns.Replace("\"Tickets\"", "\"Tickets count\"", StringComparison.Ordinal).Replace("\"type\":\"url\",\"url\"", "\"type\":\"number\",\"number\"", StringComparison.Ordinal)));
            }

            if (request.Method == HttpMethod.Get && path == $"databases/{DatabaseId}")
            {
                return Ok(Raw("""{"icon":{"type":"emoji","emoji":"🧭"}}"""));
            }

            return request.Method == HttpMethod.Patch ? Ok(Raw("""{"id":"x"}""")) : StubHttpHandler.NotFound();
        });

        var text = TextOf(await CallAsync("update_data_source", new JsonObject
        {
            ["data_source_id"] = DataSourceId,
            ["title"] = "Deliveries",
            ["icon"] = "🧭",
            ["rename_properties"] = Raw("""{"Tickets":"Tickets count"}"""),
            ["change_property_types"] = Raw("""{"Tickets":"number"}"""),
            ["set_select_options"] = Raw("""{"Status":["Reviewed","Highlight"]}"""),
        }));

        var patches = _factory.Notion.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Equal(["data_sources/" + DataSourceId, "databases/" + DatabaseId], patches.Select(p => p.Path));

        var dataSourceBody = JsonNode.Parse(patches[0].Body!)!;
        Assert.Equal("Deliveries", (string)dataSourceBody["title"]![0]!["text"]!["content"]!);
        Assert.Equal("🧭", (string)dataSourceBody["icon"]!["emoji"]!);
        Assert.Equal("Tickets count", (string)dataSourceBody["properties"]!["Tickets"]!["name"]!);
        Assert.NotNull(dataSourceBody["properties"]!["Tickets"]!["number"]);
        var options = (JsonArray)dataSourceBody["properties"]!["Status"]!["select"]!["options"]!;
        Assert.Equal("id-b", (string)options[0]!["id"]!);
        Assert.Equal("green", (string)options[0]!["color"]!);
        Assert.Equal("Highlight", (string)options[1]!["name"]!);

        var databaseBody = JsonNode.Parse(patches[1].Body!)!;
        Assert.Equal("Deliveries", (string)databaseBody["title"]![0]!["text"]!["content"]!);
        Assert.Equal("🧭", (string)databaseBody["icon"]!["emoji"]!);
        Assert.Null(databaseBody["properties"]);

        Assert.Contains("BEFORE\n# Career Track", text, StringComparison.Ordinal);
        Assert.Contains("AFTER\n# Deliveries", text, StringComparison.Ordinal);
        Assert.Contains("- Tickets (url)", text, StringComparison.Ordinal);
        Assert.Contains("- Tickets count (number)", text, StringComparison.Ordinal);
        Assert.Contains("options removed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_data_source_resolves_a_database_id_and_skips_the_database_call_for_a_schema_only_change()
    {
        Route((method, path) => path switch
        {
            _ when path == $"data_sources/{DatabaseId}" => StubHttpHandler.NotFound(),
            _ when path == $"databases/{DatabaseId}" && method == "GET" => Ok(Raw($$"""{"data_sources":[{"id":"{{DataSourceId}}","name":"T"}]}""")),
            _ when path == $"data_sources/{DataSourceId}" && method == "GET" => Ok(DataSource("T")),
            _ when path == $"data_sources/{DataSourceId}" && method == "PATCH" => Ok(Raw("""{"id":"x"}""")),
            _ => null,
        });

        await CallAsync("update_data_source", new JsonObject { ["data_source_id"] = DatabaseId, ["remove_properties"] = new JsonArray("Tickets") });

        var patch = Assert.Single(_factory.Notion.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Equal("data_sources/" + DataSourceId, patch.Path);
        Assert.Equal("""{"properties":{"Tickets":null}}""", JsonNode.Parse(patch.Body!)!.ToJsonString());
        Assert.All(_factory.Notion.Requests, r => Assert.Equal("2026-03-11", r.NotionVersion));
    }

    [Fact]
    public async Task Update_data_source_without_any_change_is_an_error_and_sends_no_patch()
    {
        Route((_, path) => path == $"data_sources/{DataSourceId}" ? Ok(DataSource("T")) : null);

        var response = await CallAsync("update_data_source", new JsonObject { ["data_source_id"] = DataSourceId });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("Nothing to update", TextOf(response), StringComparison.Ordinal);
        Assert.DoesNotContain(_factory.Notion.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Update_data_source_reports_an_unknown_column_without_calling_patch()
    {
        Route((_, path) => path == $"data_sources/{DataSourceId}" ? Ok(DataSource("T")) : null);

        var response = await CallAsync("update_data_source", new JsonObject
        {
            ["data_source_id"] = DataSourceId,
            ["rename_properties"] = Raw("""{"Nope":"X"}"""),
        });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("Columns: Name (title), Tickets (url), Status (select)", TextOf(response), StringComparison.Ordinal);
        Assert.DoesNotContain(_factory.Notion.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Update_data_source_returns_the_original_notion_message_with_the_sharing_hint()
    {
        Route((method, path) => path switch
        {
            _ when method == "GET" && path == $"data_sources/{DataSourceId}" => Ok(DataSource("T")),
            _ when method == "PATCH" => StubHttpHandler.NotFound(),
            _ => null,
        });

        var response = await CallAsync("update_data_source", new JsonObject { ["data_source_id"] = DataSourceId, ["title"] = "X" });

        Assert.True(McpTestClient.ToolIsError(response));
        var text = TextOf(response);
        Assert.Contains("Notion API error 404 (object_not_found): Not found.", text, StringComparison.Ordinal);
        Assert.Contains("Connections", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_data_source_says_what_is_missing_when_only_the_database_call_fails()
    {
        Route((method, path) => path switch
        {
            _ when method == "GET" && path == $"data_sources/{DataSourceId}" => Ok(DataSource("T")),
            _ when method == "PATCH" && path == $"data_sources/{DataSourceId}" => Ok(Raw("""{"id":"x"}""")),
            _ when method == "PATCH" && path == $"databases/{DatabaseId}" => StubHttpHandler.Json(HttpStatusCode.BadRequest, """{"code":"validation_error","message":"bad icon"}"""),
            _ => null,
        });

        var response = await CallAsync("update_data_source", new JsonObject { ["data_source_id"] = DataSourceId, ["title"] = "X" });

        var text = TextOf(response);
        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("data source", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("was updated, but the database", text, StringComparison.Ordinal);
        Assert.Contains("bad icon", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_description_of_update_data_source_says_that_data_is_deleted()
    {
        var response = await _client.RpcAsync(_token, McpTestClient.Request(1, "tools/list"));

        var tool = ((JsonArray)response["result"]!["tools"]!).Single(t => string.Equals((string)t!["name"]!, "update_data_source", StringComparison.Ordinal))!;
        var description = (string)tool["description"]!;
        Assert.Contains("remove_properties deletes", description, StringComparison.Ordinal);
        Assert.Contains("set_select_options", description, StringComparison.Ordinal);
        Assert.Contains("DELETE", (string)tool["inputSchema"]!["properties"]!["remove_properties"]!["description"]!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ create_database

    [Fact]
    public async Task Create_database_posts_the_parent_title_icon_and_initial_properties_and_returns_both_ids()
    {
        Route((method, path) => (method, path) == ("POST", "databases")
            ? Ok(Raw($$"""{"id":"{{DatabaseId}}","url":"https://notion.so/db","data_sources":[{"id":"{{DataSourceId}}","name":"Career"}]}"""))
            : null);

        var text = TextOf(await CallAsync("create_database", new JsonObject
        {
            ["parent_page_id"] = ParentId,
            ["title"] = "Career",
            ["icon"] = "🧭",
            ["properties"] = Raw("""{"Name":"title","Status":{"select":["Todo","Done"]},"Hours":"number"}"""),
        }));

        var body = BodyOf(0);
        Assert.Equal("page_id", (string)body["parent"]!["type"]!);
        Assert.Equal(ParentId, (string)body["parent"]!["page_id"]!);
        Assert.Equal("Career", (string)body["title"]![0]!["text"]!["content"]!);
        Assert.Equal("🧭", (string)body["icon"]!["emoji"]!);
        var properties = body["initial_data_source"]!["properties"]!;
        Assert.NotNull(properties["Name"]!["title"]);
        Assert.Equal("Todo", (string)properties["Status"]!["select"]!["options"]![0]!["name"]!);
        Assert.NotNull(properties["Hours"]!["number"]);
        Assert.Contains($"database_id: {DatabaseId}", text, StringComparison.Ordinal);
        Assert.Contains($"data_source_id: {DataSourceId}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_database_without_a_single_title_column_is_an_error_and_sends_nothing()
    {
        var response = await CallAsync("create_database", new JsonObject
        {
            ["parent_page_id"] = ParentId,
            ["title"] = "Career",
            ["properties"] = Raw("""{"Hours":"number"}"""),
        });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Empty(_factory.Notion.Requests);
    }

    // ------------------------------------------------------------------ fetch_data_source

    [Fact]
    public async Task Fetch_data_source_shows_option_colors_and_the_database_icon()
    {
        Route((_, path) => path switch
        {
            _ when path == $"data_sources/{DataSourceId}" => Ok(DataSource("Career")),
            _ when path == $"databases/{DatabaseId}" => Ok(Raw("""{"icon":{"type":"external","external":{"url":"https://example.com/i.png"}}}""")),
            _ => null,
        });

        var text = TextOf(await CallAsync("fetch_data_source", new JsonObject { ["data_source_id"] = DataSourceId }));

        Assert.Contains("icon: https://example.com/i.png", text, StringComparison.Ordinal);
        Assert.Contains("- Status (select): Todo (red) | Reviewed (green)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_data_source_still_works_when_the_database_cannot_be_read()
    {
        Route((_, path) => path == $"data_sources/{DataSourceId}" ? Ok(DataSource("Career")) : null);

        var response = await CallAsync("fetch_data_source", new JsonObject { ["data_source_id"] = DataSourceId });

        Assert.False(McpTestClient.ToolIsError(response));
        Assert.DoesNotContain("icon:", TextOf(response), StringComparison.Ordinal);
    }
}
