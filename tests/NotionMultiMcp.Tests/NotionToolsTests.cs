using System.Net;
using System.Text.Json.Nodes;
using NotionMultiMcp.Tests.Support;

namespace NotionMultiMcp.Tests;

/// <summary>The Notion tools end to end: JSON-RPC in, calls to a fake Notion API out.</summary>
public sealed class NotionToolsTests : IAsyncLifetime, IDisposable
{
    private const string PageId = "1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f";
    private const string DataSourceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DatabaseId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Work = "work";

    private readonly McpAppFactory _factory = new();
    private readonly McpTestClient _client;
    private string _token = "";

    public NotionToolsTests()
    {
        _client = new McpTestClient(_factory);
    }

    public async Task InitializeAsync() => _token = (await _client.SignInAsync()).Tokens.AccessToken;

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _factory.Dispose();

    // ------------------------------------------------------------------ helpers

    private static JsonObject Raw(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject TitleProperty(string text) => new()
    {
        ["type"] = "title",
        ["title"] = new JsonArray(new JsonObject { ["plain_text"] = text }),
    };

    private static JsonObject Page(string id, string title, JsonObject? properties = null, bool inTrash = false)
    {
        var all = new JsonObject { ["Name"] = TitleProperty(title) };
        foreach (var (key, value) in properties ?? [])
        {
            all[key] = value?.DeepClone();
        }

        return new JsonObject
        {
            ["id"] = id,
            ["url"] = $"https://notion.so/{id}",
            ["last_edited_time"] = "2026-09-30T10:00:00Z",
            ["in_trash"] = inTrash,
            ["parent"] = new JsonObject { ["type"] = "page_id", ["page_id"] = "parent-page" },
            ["properties"] = all,
        };
    }

    private static JsonObject DataSource(string id, string title, string propertiesJson, string? databaseId = null)
    {
        var source = new JsonObject
        {
            ["id"] = id,
            ["title"] = new JsonArray(new JsonObject { ["plain_text"] = title }),
            ["properties"] = Raw(propertiesJson),
        };
        if (databaseId is not null)
        {
            source["parent"] = new JsonObject { ["database_id"] = databaseId };
        }

        return source;
    }

    private static JsonObject Paragraph(string text, string id = "b") => new()
    {
        ["id"] = id,
        ["type"] = "paragraph",
        ["has_children"] = false,
        ["paragraph"] = new JsonObject
        {
            ["rich_text"] = new JsonArray(new JsonObject { ["type"] = "text", ["plain_text"] = text, ["annotations"] = new JsonObject() }),
        },
    };

    private static JsonObject Children(params JsonObject[] blocks) =>
        new() { ["results"] = new JsonArray([.. blocks.Select(b => (JsonNode)b)]), ["has_more"] = false };

    private static HttpResponseMessage Ok(JsonNode json) => StubHttpHandler.Ok(json.ToJsonString());

    /// <summary>Routes fake Notion calls by method and path; anything not routed is a 404.</summary>
    private void Route(Func<string, string, HttpResponseMessage?> routes) =>
        _factory.Notion.Respond(request => routes(request.Method.Method, StubHttpHandler.PathOf(request)) ?? StubHttpHandler.NotFound());

    private async Task<JsonNode> CallAsync(string tool, JsonObject arguments)
    {
        arguments["workspace"] = Work;
        return await _client.RpcAsync(_token, McpTestClient.ToolCall(1, tool, arguments)).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static string TextOf(JsonNode response) => McpTestClient.ToolText(response).ReplaceLineEndings("\n");

    private JsonNode BodyOf(int index) => JsonNode.Parse(_factory.Notion.Requests[index].Body!)!;

    private static string Bullets(int count) => string.Join("\n", Enumerable.Range(1, count).Select(i => $"- item {i}"));

    // ------------------------------------------------------------------ fetch_page

    [Fact]
    public async Task Fetch_page_shows_title_metadata_properties_and_content()
    {
        var properties = Raw("""
            {"Status":{"type":"status","status":{"name":"Doing"}},
             "Tags":{"type":"multi_select","multi_select":[{"name":"a"},{"name":"b"}]},
             "Done":{"type":"checkbox","checkbox":false},
             "Notes":{"type":"rich_text","rich_text":[]}}
            """);
        Route((method, path) => path switch
        {
            _ when path == $"pages/{PageId}" => Ok(Page(PageId, "Roadmap", properties)),
            _ when path.StartsWith($"blocks/{PageId}/children", StringComparison.Ordinal) =>
                Ok(Children(Raw("""{"id":"h","type":"heading_1","has_children":false,"heading_1":{"rich_text":[{"plain_text":"Goals"}]}}"""), Paragraph("Ship it."))),
            _ => null,
        });

        var text = TextOf(await CallAsync("fetch_page", new JsonObject { ["page_id"] = PageId }));

        Assert.StartsWith("# Roadmap\n", text);
        Assert.Contains($"id: {PageId}", text);
        Assert.Contains("last edited: 2026-09-30T10:00:00Z", text);
        Assert.Contains("parent: page_id parent-page", text);
        Assert.Contains("Properties:\n- Status: Doing\n- Tags: a, b\n- Done: no", text);
        Assert.DoesNotContain("Notes", text);
        Assert.Contains("---\n\n# Goals\n\nShip it.", text);
    }

    [Fact]
    public async Task Fetch_page_can_skip_the_content_and_then_reads_only_the_page()
    {
        Route((_, path) => path == $"pages/{PageId}" ? Ok(Page(PageId, "Roadmap")) : null);

        var text = TextOf(await CallAsync("fetch_page", new JsonObject { ["page_id"] = PageId, ["include_content"] = false }));

        Assert.DoesNotContain("---", text);
        Assert.Single(_factory.Notion.Requests);
    }

    [Fact]
    public async Task Fetch_page_flags_a_trashed_page_and_an_empty_one()
    {
        Route((_, path) => path switch
        {
            _ when path == $"pages/{PageId}" => Ok(Page(PageId, "Old", inTrash: true)),
            _ when path.StartsWith("blocks/", StringComparison.Ordinal) => Ok(Children()),
            _ => null,
        });

        var text = TextOf(await CallAsync("fetch_page", new JsonObject { ["page_id"] = PageId }));

        Assert.Contains("status: in trash", text);
        Assert.Contains("(empty page)", text);
    }

    [Fact]
    public async Task Fetch_page_takes_the_id_from_a_notion_url()
    {
        Route((_, path) => path == $"pages/{PageId}" ? Ok(Page(PageId, "Roadmap")) : null);

        await CallAsync("fetch_page", new JsonObject
        {
            ["page_id"] = $"https://www.notion.so/acme/Roadmap-{PageId}?pvs=4",
            ["include_content"] = false,
        });

        Assert.Equal($"pages/{PageId}", _factory.Notion.Requests.Single().Path);
    }

    [Fact]
    public async Task Output_longer_than_the_limit_is_cut_with_a_marker()
    {
        Route((_, path) => path switch
        {
            _ when path == $"pages/{PageId}" => Ok(Page(PageId, "Big")),
            _ when path.StartsWith("blocks/", StringComparison.Ordinal) => Ok(Children(Paragraph(new string('x', 70_000)))),
            _ => null,
        });

        var text = McpTestClient.ToolText(await CallAsync("fetch_page", new JsonObject { ["page_id"] = PageId }));

        Assert.EndsWith("[output truncated]", text);
        Assert.InRange(text.Length, 60_000, 60_100);
    }

    // ------------------------------------------------------------------ fetch_data_source

    private const string TaskProperties = """
        {"Name":{"type":"title","title":{}},
         "Status":{"type":"status","status":{"options":[{"name":"Todo"},{"name":"Done"}]}},
         "Tags":{"type":"multi_select","multi_select":{"options":[{"name":"x"},{"name":"y"}]}},
         "Due":{"type":"date","date":{}},
         "Project":{"type":"relation","relation":{"data_source_id":"rel-source"}}}
        """;

    [Fact]
    public async Task Fetch_data_source_lists_properties_with_options_and_relations()
    {
        Route((_, path) => path == $"data_sources/{DataSourceId}"
            ? Ok(DataSource(DataSourceId, "Tasks", TaskProperties, DatabaseId))
            : null);

        var text = TextOf(await CallAsync("fetch_data_source", new JsonObject { ["data_source_id"] = DataSourceId }));

        Assert.Contains("# Tasks", text);
        Assert.Contains($"data_source_id: {DataSourceId}", text);
        Assert.Contains($"database_id: {DatabaseId}", text);
        Assert.Contains("- Name (title)", text);
        Assert.Contains("- Status (status): Todo | Done", text);
        Assert.Contains("- Tags (multi_select): x | y", text);
        Assert.Contains("- Due (date)", text);
        Assert.Contains("- Project (relation) → data source rel-source", text);
    }

    [Fact]
    public async Task A_database_id_with_one_data_source_resolves_to_it()
    {
        Route((_, path) => path switch
        {
            _ when path == $"databases/{DatabaseId}" => Ok(Raw($$"""{"data_sources":[{"id":"{{DataSourceId}}","name":"Tasks"}]}""")),
            _ when path == $"data_sources/{DataSourceId}" => Ok(DataSource(DataSourceId, "Tasks", TaskProperties)),
            _ => null,
        });

        var text = TextOf(await CallAsync("fetch_data_source", new JsonObject { ["data_source_id"] = DatabaseId }));

        Assert.Contains($"data_source_id: {DataSourceId}", text);
        Assert.Equal(
            [$"data_sources/{DatabaseId}", $"databases/{DatabaseId}", $"data_sources/{DataSourceId}"],
            _factory.Notion.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task A_database_with_several_data_sources_asks_which_one_to_use()
    {
        Route((_, path) => path == $"databases/{DatabaseId}"
            ? Ok(Raw("""{"data_sources":[{"id":"ds-1","name":"Tasks"},{"id":"ds-2","name":"Archive"}]}"""))
            : null);

        var response = await CallAsync("fetch_data_source", new JsonObject { ["data_source_id"] = DatabaseId });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("Tasks = ds-1, Archive = ds-2", TextOf(response));
    }

    [Fact]
    public async Task A_database_without_data_sources_is_an_error()
    {
        Route((_, path) => path == $"databases/{DatabaseId}" ? Ok(Raw("""{"data_sources":[]}""")) : null);

        var response = await CallAsync("fetch_data_source", new JsonObject { ["data_source_id"] = DatabaseId });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("no data sources visible", TextOf(response));
    }

    [Fact]
    public async Task An_id_that_is_neither_a_data_source_nor_a_database_reports_the_first_error()
    {
        Route((_, _) => null);

        var response = await CallAsync("fetch_data_source", new JsonObject { ["data_source_id"] = DatabaseId });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("object_not_found", TextOf(response));
        Assert.Contains("Connections", TextOf(response));
    }

    // ------------------------------------------------------------------ query_data_source

    private void RouteTasks(JsonObject queryResult) => Route((method, path) => (method, path) switch
    {
        ("GET", _) when path == $"data_sources/{DataSourceId}" => Ok(DataSource(DataSourceId, "Tasks", TaskProperties)),
        ("POST", _) when path == $"data_sources/{DataSourceId}/query" => Ok(queryResult),
        _ => null,
    });

    [Fact]
    public async Task Query_sends_the_filter_sorts_limit_and_cursor_and_formats_the_rows()
    {
        var row = Page("row-1", "Write tests", Raw("""{"Status":{"type":"status","status":{"name":"Doing"}},"Due":{"type":"date","date":{"start":"2026-10-01"}}}"""));
        RouteTasks(new JsonObject { ["results"] = new JsonArray(row), ["has_more"] = true, ["next_cursor"] = "cursor-2" });

        var text = TextOf(await CallAsync("query_data_source", new JsonObject
        {
            ["data_source_id"] = DataSourceId,
            ["filter"] = Raw("""{"property":"Status","status":{"equals":"Doing"}}"""),
            ["sorts"] = new JsonArray(Raw("""{"property":"Due","direction":"ascending"}""")),
            ["limit"] = 10,
            ["cursor"] = "cursor-1",
        }));

        Assert.Contains($"Tasks (data_source_id: {DataSourceId}): 1 row(s)", text);
        Assert.Contains("- **Write tests** (id: row-1) — Status: Doing; Due: 2026-10-01", text);
        Assert.Contains("next_cursor: cursor-2", text);
        var body = BodyOf(1);
        Assert.Equal(10, (int)body["page_size"]!);
        Assert.Equal("cursor-1", (string)body["start_cursor"]!);
        Assert.Equal("Doing", (string)body["filter"]!["status"]!["equals"]!);
        Assert.Equal("ascending", (string)body["sorts"]![0]!["direction"]!);
    }

    [Fact]
    public async Task Query_accepts_the_filter_and_sorts_given_as_json_text()
    {
        RouteTasks(new JsonObject { ["results"] = new JsonArray(), ["has_more"] = false });

        await CallAsync("query_data_source", new JsonObject
        {
            ["data_source_id"] = DataSourceId,
            ["filter"] = """{"property":"Status","status":{"equals":"Done"}}""",
            ["sorts"] = """[{"property":"Due","direction":"descending"}]""",
        });

        var body = BodyOf(1);
        Assert.Equal("Done", (string)body["filter"]!["status"]!["equals"]!);
        Assert.Equal("descending", (string)body["sorts"]![0]!["direction"]!);
    }

    [Fact]
    public async Task Query_without_a_filter_sends_only_the_page_size()
    {
        RouteTasks(new JsonObject { ["results"] = new JsonArray(), ["has_more"] = false });

        var text = TextOf(await CallAsync("query_data_source", new JsonObject { ["data_source_id"] = DataSourceId }));

        Assert.Contains("0 row(s)", text);
        var body = (JsonObject)BodyOf(1);
        Assert.Equal(["page_size"], body.Select(p => p.Key));
        Assert.Equal(25, (int)body["page_size"]!);
    }

    [Fact]
    public async Task A_filter_that_is_not_valid_json_is_a_tool_error()
    {
        RouteTasks(new JsonObject { ["results"] = new JsonArray() });

        var response = await CallAsync("query_data_source", new JsonObject { ["data_source_id"] = DataSourceId, ["filter"] = "{not json" });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("not valid JSON", TextOf(response));
    }

    // ------------------------------------------------------------------ create_page

    [Fact]
    public async Task Create_page_in_a_data_source_converts_simple_property_values()
    {
        Route((method, path) => (method, path) switch
        {
            ("GET", _) when path == $"data_sources/{DataSourceId}" => Ok(DataSource(DataSourceId, "Tasks", """
                {"Name":{"type":"title","title":{}},"Status":{"type":"status","status":{}},"Tags":{"type":"multi_select","multi_select":{}},
                 "Due":{"type":"date","date":{}},"Done":{"type":"checkbox","checkbox":{}},"Points":{"type":"number","number":{}}}
                """)),
            ("POST", "pages") => Ok(Page("new-page", "Write tests")),
            _ => null,
        });

        var response = await CallAsync("create_page", new JsonObject
        {
            ["parent_data_source_id"] = DataSourceId,
            ["title"] = "Write tests",
            ["properties"] = Raw("""{"Status":"Todo","Tags":["a","b"],"Due":"2026-10-01","Done":true,"Points":"3"}"""),
        });

        Assert.Contains("id: new-page", TextOf(response));
        var body = BodyOf(1);
        Assert.Equal("data_source_id", (string)body["parent"]!["type"]!);
        Assert.Equal(DataSourceId, (string)body["parent"]!["data_source_id"]!);
        var properties = body["properties"]!;
        Assert.Equal("Write tests", (string)properties["Name"]!["title"]![0]!["text"]!["content"]!);
        Assert.Equal("Todo", (string)properties["Status"]!["status"]!["name"]!);
        Assert.Equal(["a", "b"], properties["Tags"]!["multi_select"]!.AsArray().Select(t => (string)t!["name"]!));
        Assert.Equal("2026-10-01", (string)properties["Due"]!["date"]!["start"]!);
        Assert.True((bool)properties["Done"]!["checkbox"]!);
        Assert.Equal(3.0, (double)properties["Points"]!["number"]!);
    }

    [Fact]
    public async Task Create_page_rejects_an_unknown_property_and_lists_the_available_ones()
    {
        Route((_, path) => path == $"data_sources/{DataSourceId}" ? Ok(DataSource(DataSourceId, "Tasks", TaskProperties)) : null);

        var response = await CallAsync("create_page", new JsonObject
        {
            ["parent_data_source_id"] = DataSourceId,
            ["properties"] = Raw("""{"Nope":"x"}"""),
        });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("Unknown property 'Nope'", TextOf(response));
        Assert.Contains("Status (status)", TextOf(response));
        Assert.Single(_factory.Notion.Requests);
    }

    [Fact]
    public async Task Create_page_rejects_properties_when_the_parent_is_a_page()
    {
        var response = await CallAsync("create_page", new JsonObject
        {
            ["parent_page_id"] = PageId,
            ["properties"] = Raw("""{"Status":"Todo"}"""),
        });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("only applies when the parent is a data source", TextOf(response));
        Assert.Empty(_factory.Notion.Requests);
    }

    [Fact]
    public async Task Create_page_without_a_title_uses_untitled()
    {
        Route((method, path) => (method, path) == ("POST", "pages") ? Ok(Page("new-page", "Untitled")) : null);

        await CallAsync("create_page", new JsonObject { ["parent_page_id"] = PageId });

        Assert.Equal("Untitled", (string)BodyOf(0)["properties"]!["title"]!["title"]![0]!["text"]!["content"]!);
    }

    [Fact]
    public async Task Create_page_sends_long_content_in_batches_of_one_hundred_blocks()
    {
        Route((method, path) => (method, path) switch
        {
            ("POST", "pages") => Ok(Page("new-page", "Long")),
            ("PATCH", "blocks/new-page/children") => Ok(Raw("""{"results":[]}""")),
            _ => null,
        });

        var response = await CallAsync("create_page", new JsonObject
        {
            ["parent_page_id"] = PageId,
            ["title"] = "Long",
            ["content_markdown"] = Bullets(250),
        });

        Assert.False(McpTestClient.ToolIsError(response));
        Assert.Equal(
            [("POST", "pages"), ("PATCH", "blocks/new-page/children"), ("PATCH", "blocks/new-page/children")],
            _factory.Notion.Requests.Select(r => (r.Method.Method, r.Path)));
        Assert.Equal([100, 100, 50], [BodyOf(0)["children"]!.AsArray().Count, BodyOf(1)["children"]!.AsArray().Count, BodyOf(2)["children"]!.AsArray().Count]);
    }

    // ------------------------------------------------------------------ append_content

    [Fact]
    public async Task Append_content_adds_blocks_in_batches_and_reports_the_count()
    {
        Route((method, path) => (method, path) == ("PATCH", $"blocks/{PageId}/children") ? Ok(Raw("""{"results":[]}""")) : null);

        var text = TextOf(await CallAsync("append_content", new JsonObject { ["page_id"] = PageId, ["content_markdown"] = Bullets(205) }));

        Assert.Equal($"Appended 205 block(s) to page {PageId}.", text);
        Assert.Equal([100, 100, 5], Enumerable.Range(0, 3).Select(i => BodyOf(i)["children"]!.AsArray().Count));
    }

    [Fact]
    public async Task Append_content_rejects_content_that_produces_no_blocks()
    {
        var response = await CallAsync("append_content", new JsonObject { ["page_id"] = PageId, ["content_markdown"] = "   \n\n  " });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("produced no blocks", TextOf(response));
        Assert.Empty(_factory.Notion.Requests);
    }

    // ------------------------------------------------------------------ update_page

    [Fact]
    public async Task Update_page_converts_properties_with_the_page_schema_and_sets_the_title()
    {
        var current = Page(PageId, "Old name", Raw("""{"Status":{"type":"status","status":{"name":"Todo"}}}"""));
        Route((method, path) => (method, path) switch
        {
            ("GET", _) when path == $"pages/{PageId}" => Ok(current),
            ("PATCH", _) when path == $"pages/{PageId}" => Ok(Page(PageId, "New name")),
            _ => null,
        });

        var text = TextOf(await CallAsync("update_page", new JsonObject
        {
            ["page_id"] = PageId,
            ["title"] = "New name",
            ["properties"] = Raw("""{"Status":"Done"}"""),
        }));

        Assert.Contains("Updated page \"New name\"", text);
        var body = BodyOf(1);
        Assert.Equal("Done", (string)body["properties"]!["Status"]!["status"]!["name"]!);
        Assert.Equal("New name", (string)body["properties"]!["Name"]!["title"]![0]!["text"]!["content"]!);
        Assert.Null(body["in_trash"]);
    }

    [Fact]
    public async Task Update_page_can_restore_a_page_from_the_trash_without_reading_it_first()
    {
        Route((method, path) => (method, path) == ("PATCH", $"pages/{PageId}") ? Ok(Page(PageId, "Back")) : null);

        var text = TextOf(await CallAsync("update_page", new JsonObject { ["page_id"] = PageId, ["in_trash"] = false }));

        Assert.Contains("(restored)", text);
        var request = _factory.Notion.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.False((bool)BodyOf(0)["in_trash"]!);
    }

    [Fact]
    public async Task A_notion_rate_limit_is_retried_and_the_call_still_succeeds()
    {
        _factory.Notion.Respond(
            _ => StubHttpHandler.Json((HttpStatusCode)429, "{}"),
            _ => Ok(Page(PageId, "Roadmap")));
        var call = CallAsync("fetch_page", new JsonObject { ["page_id"] = PageId, ["include_content"] = false });

        while (!call.IsCompleted)
        {
            _factory.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAny(call, Task.Delay(1));
        }

        Assert.Contains("# Roadmap", TextOf(await call));
        Assert.Equal(2, _factory.Notion.Requests.Count);
    }

    // ------------------------------------------------------------------ move_page

    private const string NewParentId = "cccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task Move_page_posts_the_new_parent_page_to_the_move_endpoint()
    {
        Route((method, path) => (method, path) == ("POST", $"pages/{PageId}/move") ? Ok(Page(PageId, "Letter v1.1")) : null);

        var text = TextOf(await CallAsync("move_page", new JsonObject
        {
            ["page_id"] = $"https://www.notion.so/acme/Letter-{PageId}",
            ["new_parent_page_id"] = NewParentId,
        }));

        Assert.Contains($"Moved page \"Letter v1.1\" under page {NewParentId}", text);
        Assert.Contains($"id: {PageId}", text);
        Assert.Contains($"url: https://notion.so/{PageId}", text);
        var request = _factory.Notion.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        var parent = BodyOf(0)["parent"]!;
        Assert.Equal("page_id", (string)parent["type"]!);
        Assert.Equal(NewParentId, (string)parent["page_id"]!);
    }

    [Fact]
    public async Task Move_page_resolves_a_database_id_to_its_data_source()
    {
        Route((method, path) => (method, path) switch
        {
            ("GET", _) when path == $"databases/{DatabaseId}" => Ok(Raw($$"""{"data_sources":[{"id":"{{DataSourceId}}","name":"Tasks"}]}""")),
            ("GET", _) when path == $"data_sources/{DataSourceId}" => Ok(DataSource(DataSourceId, "Tasks", TaskProperties)),
            ("POST", _) when path == $"pages/{PageId}/move" => Ok(Raw($$"""{"object":"page","id":"{{PageId}}"}""")),
            _ => null,
        });

        var text = TextOf(await CallAsync("move_page", new JsonObject
        {
            ["page_id"] = PageId,
            ["new_parent_data_source_id"] = DatabaseId,
        }));

        Assert.Equal($"Moved page under data source {DataSourceId}\nid: {PageId}", text);
        var move = _factory.Notion.Requests.Last();
        Assert.Equal($"pages/{PageId}/move", move.Path);
        var parent = JsonNode.Parse(move.Body!)!["parent"]!;
        Assert.Equal("data_source_id", (string)parent["type"]!);
        Assert.Equal(DataSourceId, (string)parent["data_source_id"]!);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(NewParentId, DataSourceId)]
    public async Task Move_page_needs_exactly_one_new_parent(string? newParentPage, string? newParentDataSource)
    {
        var arguments = new JsonObject { ["page_id"] = PageId };
        if (newParentPage is not null)
        {
            arguments["new_parent_page_id"] = newParentPage;
        }

        if (newParentDataSource is not null)
        {
            arguments["new_parent_data_source_id"] = newParentDataSource;
        }

        var response = await CallAsync("move_page", arguments);

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("exactly one of new_parent_page_id or new_parent_data_source_id", TextOf(response));
        Assert.Empty(_factory.Notion.Requests);
    }

    [Fact]
    public async Task Move_page_refuses_to_move_a_page_under_itself()
    {
        var response = await CallAsync("move_page", new JsonObject
        {
            ["page_id"] = PageId,
            ["new_parent_page_id"] = "1a2b3c4d-1a2b-1a2b-1a2b-1a2b3c4d5e6f",
        });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("cannot be moved under itself", TextOf(response));
        Assert.Empty(_factory.Notion.Requests);
    }

    [Fact]
    public async Task Move_page_is_not_retried_after_a_server_error()
    {
        Route((method, path) => (method, path) == ("POST", $"pages/{PageId}/move") ? _batchFails(null!) : null);

        var response = await CallAsync("move_page", new JsonObject { ["page_id"] = PageId, ["new_parent_page_id"] = NewParentId });

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Single(_factory.Notion.Requests);
    }

    // ------------------------------------------------------------------ a write that only partly landed

    private static readonly Func<HttpRequestMessage, HttpResponseMessage> _batchFails =
        _ => StubHttpHandler.Json(HttpStatusCode.InternalServerError, """{"code":"internal_server_error","message":"Notion is having a problem."}""");

    /// <summary>Routes create_page so that the page is created and the nth batch append (1-based) fails.</summary>
    private void RouteCreateWithFailingBatch(int failingBatch)
    {
        var patches = 0;
        _factory.Notion.Respond(request => (request.Method.Method, StubHttpHandler.PathOf(request)) switch
        {
            ("POST", "pages") => Ok(Page("new-page", "Long")),
            ("PATCH", "blocks/new-page/children") when ++patches == failingBatch => _batchFails(request),
            ("PATCH", "blocks/new-page/children") => Ok(Raw("""{"results":[]}""")),
            _ => StubHttpHandler.NotFound(),
        });
    }

    [Fact]
    public async Task A_failure_after_the_page_was_created_tells_the_model_the_page_exists_and_how_far_it_got()
    {
        RouteCreateWithFailingBatch(failingBatch: 2);

        var response = await CallAsync("create_page", new JsonObject
        {
            ["parent_page_id"] = PageId,
            ["title"] = "Long",
            ["content_markdown"] = Bullets(250),
        });
        var text = TextOf(response);

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("id: new-page", text);
        Assert.Contains("https://notion.so/new-page", text);
        Assert.Contains("only 200 of 250 blocks", text);
        Assert.Contains("Do not create the page again", text);
        Assert.Contains("append_content", text);
        Assert.Equal(3, _factory.Notion.Requests.Count);
    }

    [Fact]
    public async Task A_failure_on_the_first_extra_batch_still_counts_the_blocks_sent_with_the_page()
    {
        RouteCreateWithFailingBatch(failingBatch: 1);

        var text = TextOf(await CallAsync("create_page", new JsonObject
        {
            ["parent_page_id"] = PageId,
            ["title"] = "Long",
            ["content_markdown"] = Bullets(150),
        }));

        Assert.Contains("only 100 of 150 blocks", text);
    }

    [Fact]
    public async Task A_timeout_while_adding_blocks_says_the_batch_may_have_been_applied()
    {
        _factory.Notion.Respond(request => StubHttpHandler.PathOf(request) == "pages"
            ? Ok(Page("new-page", "Long"))
            : throw new TaskCanceledException("timed out"));

        var text = TextOf(await CallAsync("create_page", new JsonObject
        {
            ["parent_page_id"] = PageId,
            ["title"] = "Long",
            ["content_markdown"] = Bullets(150),
        }));

        Assert.Contains("id: new-page", text);
        Assert.Contains("may or may not have been applied", text);
    }

    [Fact]
    public async Task A_failure_while_appending_says_how_many_blocks_landed()
    {
        var patches = 0;
        Route((method, path) => (method, path) == ("PATCH", $"blocks/{PageId}/children")
            ? (++patches == 2 ? _batchFails(null!) : Ok(Raw("""{"results":[]}""")))
            : null);

        var response = await CallAsync("append_content", new JsonObject { ["page_id"] = PageId, ["content_markdown"] = Bullets(205) });
        var text = TextOf(response);

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("only 100 of 205 blocks", text);
        Assert.Contains($"page {PageId}", text);
        Assert.Contains("Do not repeat the whole call", text);
        Assert.Equal(2, _factory.Notion.Requests.Count);
    }

    [Fact]
    public async Task A_failure_on_the_first_batch_of_an_append_is_the_plain_notion_error()
    {
        Route((method, path) => (method, path) == ("PATCH", $"blocks/{PageId}/children") ? _batchFails(null!) : null);

        var text = TextOf(await CallAsync("append_content", new JsonObject { ["page_id"] = PageId, ["content_markdown"] = Bullets(10) }));

        Assert.Contains("Notion API error 500", text);
        Assert.DoesNotContain("only", text);
    }

    [Fact]
    public async Task Fetch_page_shows_a_marker_for_a_property_type_it_cannot_read()
    {
        Route((_, path) => path == $"pages/{PageId}"
            ? Ok(Page(PageId, "Roadmap", Raw("""{"Go":{"type":"button","button":{}}}""")))
            : null);

        var text = TextOf(await CallAsync("fetch_page", new JsonObject { ["page_id"] = PageId, ["include_content"] = false }));

        Assert.Contains("- Go: [unsupported property type: button]", text);
    }
}
