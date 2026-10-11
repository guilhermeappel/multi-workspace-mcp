using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using NotionMultiMcp.Notion;
using NotionMultiMcp.Tests.Support;

namespace NotionMultiMcp.Tests;

public sealed class BlockRendererTests
{
    private const string Token = "ntn_test";

    private readonly StubHttpHandler _handler = new();

    private BlockRenderer CreateRenderer()
    {
        var client = new NotionClient(
            new HttpClient(_handler) { BaseAddress = NotionClient.BaseAddress },
            new FakeTimeProvider());
        return new BlockRenderer(client, Token);
    }

    // ------------------------------------------------------------------ builders

    private static JsonObject Raw(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject Item(string plain, string? annotation = null) => new()
    {
        ["type"] = "text",
        ["plain_text"] = plain,
        ["annotations"] = annotation is null ? [] : new JsonObject { [annotation] = true },
    };

    private static JsonArray Rich(params JsonObject[] items) => new([.. items]);

    private static JsonArray Rich(string plain) => Rich(Item(plain));

    private static JsonObject Block(string type, JsonArray richText, JsonObject? extra = null, bool hasChildren = false, string id = "b")
    {
        var data = new JsonObject { ["rich_text"] = richText };
        foreach (var (key, value) in extra ?? [])
        {
            data[key] = value?.DeepClone();
        }

        return new JsonObject { ["id"] = id, ["type"] = type, ["has_children"] = hasChildren, [type] = data };
    }

    private static JsonObject Paragraph(string text, bool hasChildren = false, string id = "b") =>
        Block("paragraph", Rich(text), hasChildren: hasChildren, id: id);

    private static JsonObject Bullet(string text, bool hasChildren = false, string id = "b") =>
        Block("bulleted_list_item", Rich(text), hasChildren: hasChildren, id: id);

    // ------------------------------------------------------------------ rendering

    private static string Normalize(string text) => text.ReplaceLineEndings("\n");

    /// <summary>Serves the given children per parent block id, as a single page each.</summary>
    private void Serve(Dictionary<string, JsonObject[]> childrenByParent)
    {
        _handler.Respond(request =>
        {
            var path = StubHttpHandler.PathOf(request);
            var parent = path["blocks/".Length..path.IndexOf("/children", StringComparison.Ordinal)];
            if (!childrenByParent.TryGetValue(parent, out var children))
            {
                return StubHttpHandler.NotFound();
            }

            var page = new JsonObject
            {
                ["results"] = new JsonArray([.. children.Select(c => (JsonNode)c.DeepClone())]),
                ["has_more"] = false,
            };
            return StubHttpHandler.Ok(page.ToJsonString());
        });
    }

    private async Task<string> RenderAsync(Dictionary<string, JsonObject[]> childrenByParent)
    {
        Serve(childrenByParent);
        return Normalize(await CreateRenderer().RenderAsync("root", CancellationToken.None));
    }

    private Task<string> RenderTopLevelAsync(params JsonObject[] blocks) =>
        RenderAsync(new Dictionary<string, JsonObject[]> { ["root"] = blocks });

    // ------------------------------------------------------------------ text blocks

    [Fact]
    public async Task Headings_paragraphs_and_dividers_are_rendered_as_markdown()
    {
        var markdown = await RenderTopLevelAsync(
            Block("heading_1", Rich("Title")),
            Block("heading_2", Rich("Section")),
            Block("heading_3", Rich("Detail")),
            Paragraph("Some text."),
            Raw("""{"id":"d","type":"divider","has_children":false,"divider":{}}"""));

        Assert.Equal("# Title\n\n## Section\n\n### Detail\n\nSome text.\n\n---", markdown);
    }

    [Fact]
    public async Task Numbered_items_count_up_and_restart_after_another_block()
    {
        var markdown = await RenderTopLevelAsync(
            Block("numbered_list_item", Rich("one")),
            Block("numbered_list_item", Rich("two")),
            Paragraph("break"),
            Block("numbered_list_item", Rich("again")));

        Assert.Equal("1. one\n2. two\nbreak\n\n1. again", markdown);
    }

    [Fact]
    public async Task Bullets_and_to_dos_are_rendered_as_list_items()
    {
        var markdown = await RenderTopLevelAsync(
            Bullet("item"),
            Block("to_do", Rich("open"), new JsonObject { ["checked"] = false }),
            Block("to_do", Rich("done"), new JsonObject { ["checked"] = true }));

        Assert.Equal("- item\n- [ ] open\n- [x] done", markdown);
    }

    [Fact]
    public async Task Quotes_and_callouts_keep_every_line_inside_the_quote()
    {
        var markdown = await RenderTopLevelAsync(
            Block("quote", Rich("first\nsecond")),
            Block("callout", Rich("careful"), new JsonObject { ["icon"] = new JsonObject { ["emoji"] = "⚠️" } }));

        Assert.Equal("> first\n> second\n\n> ⚠️ careful", markdown);
    }

    [Fact]
    public async Task Code_blocks_keep_their_language_and_their_raw_text()
    {
        var markdown = await RenderTopLevelAsync(
            Block("code", Rich(Item("var x = 1;", "bold")), new JsonObject { ["language"] = "c#" }));

        Assert.Equal("```c#\nvar x = 1;\n```", markdown);
    }

    [Fact]
    public async Task Equations_are_rendered_between_double_dollars()
    {
        var markdown = await RenderTopLevelAsync(Raw("""{"id":"e","type":"equation","has_children":false,"equation":{"expression":"a^2+b^2"}}"""));

        Assert.Equal("$$a^2+b^2$$", markdown);
    }

    [Fact]
    public async Task Inline_annotations_are_rendered()
    {
        var markdown = await RenderTopLevelAsync(Block(
            "paragraph",
            Rich(
                Item("plain "),
                Item("bold", "bold"),
                Item(" "),
                Item("it", "italic"),
                Item(" "),
                Item("code", "code"),
                Item(" "),
                Item("gone", "strikethrough"))));

        Assert.Equal("plain **bold** *it* `code` ~~gone~~", markdown);
    }

    // ------------------------------------------------------------------ structure

    [Fact]
    public async Task Children_are_indented_under_their_parent()
    {
        var markdown = await RenderAsync(new Dictionary<string, JsonObject[]>
        {
            ["root"] = [Bullet("parent", hasChildren: true, id: "p")],
            ["p"] = [Bullet("child", hasChildren: true, id: "c")],
            ["c"] = [Paragraph("grandchild")],
        });

        Assert.Equal("- parent\n  - child\n    grandchild", markdown);
    }

    [Fact]
    public async Task Nesting_stops_after_four_levels()
    {
        var levels = new Dictionary<string, JsonObject[]>();
        for (var i = 0; i < 8; i++)
        {
            levels[i == 0 ? "root" : $"n{i}"] = [Bullet($"level {i}", hasChildren: true, id: $"n{i + 1}")];
        }

        var markdown = await RenderAsync(levels);

        Assert.Contains("level 4", markdown);
        Assert.DoesNotContain("level 5", markdown);
    }

    [Fact]
    public async Task Columns_are_flattened_without_extra_indentation()
    {
        var markdown = await RenderAsync(new Dictionary<string, JsonObject[]>
        {
            ["root"] = [Raw("""{"id":"cl","type":"column_list","has_children":true,"column_list":{}}""")],
            ["cl"] =
            [
                Raw("""{"id":"c1","type":"column","has_children":true,"column":{}}"""),
                Raw("""{"id":"c2","type":"column","has_children":true,"column":{}}"""),
            ],
            ["c1"] = [Paragraph("left")],
            ["c2"] = [Paragraph("right")],
        });

        Assert.Equal("left\n\nright", markdown);
    }

    private static JsonObject Row(string id, params string[] cells) => new()
    {
        ["id"] = id,
        ["type"] = "table_row",
        ["table_row"] = new JsonObject { ["cells"] = new JsonArray([.. cells.Select(c => (JsonNode)Rich(c))]) },
    };

    [Fact]
    public async Task A_table_with_a_header_row_becomes_a_markdown_table()
    {
        var markdown = await RenderAsync(new Dictionary<string, JsonObject[]>
        {
            ["root"] = [Raw("""{"id":"t","type":"table","has_children":true,"table":{"has_column_header":true}}""")],
            ["t"] = [Row("r1", "Name", "Qty"), Row("r2", "a|b", "2")],
        });

        Assert.Equal("| Name | Qty |\n| --- | --- |\n| a\\|b | 2 |", markdown);
    }

    [Fact]
    public async Task A_table_without_a_header_row_says_so()
    {
        var markdown = await RenderAsync(new Dictionary<string, JsonObject[]>
        {
            ["root"] = [Raw("""{"id":"t","type":"table","has_children":true,"table":{"has_column_header":false}}""")],
            ["t"] = [Row("r1", "only")],
        });

        Assert.Contains("<!-- first row is not a header -->", markdown);
    }

    // ------------------------------------------------------------------ references and special blocks

    [Fact]
    public async Task Child_pages_and_databases_are_shown_with_their_ids()
    {
        var markdown = await RenderTopLevelAsync(
            Raw("""{"id":"page-1","type":"child_page","has_children":true,"child_page":{"title":"Notes"}}"""),
            Raw("""{"id":"db-1","type":"child_database","has_children":false,"child_database":{"title":"Tasks"}}"""));

        Assert.Contains("[child page] Notes (id: page-1)", markdown);
        Assert.Contains("[child database] Tasks (id: db-1) — use fetch_data_source", markdown);
    }

    [Fact]
    public async Task Files_and_links_are_rendered_as_markdown_links()
    {
        var markdown = await RenderTopLevelAsync(
            Raw("""{"id":"i","type":"image","has_children":false,"image":{"type":"external","external":{"url":"https://x.dev/a.png"},"caption":[{"plain_text":"A diagram"}]}}"""),
            Raw("""{"id":"f","type":"file","has_children":false,"file":{"type":"file","file":{"url":"https://files.example/f.pdf"},"name":"spec.pdf","caption":[]}}"""),
            Raw("""{"id":"bm","type":"bookmark","has_children":false,"bookmark":{"url":"https://example.com"}}"""));

        Assert.Contains("[image: A diagram](https://x.dev/a.png)", markdown);
        Assert.Contains("[file: spec.pdf](https://files.example/f.pdf)", markdown);
        Assert.Contains("[bookmark](https://example.com)", markdown);
    }

    [Fact]
    public async Task Link_to_page_shows_the_target_id()
    {
        var markdown = await RenderTopLevelAsync(Raw("""{"id":"l","type":"link_to_page","has_children":false,"link_to_page":{"type":"page_id","page_id":"target-1"}}"""));

        Assert.Equal("[link to page] (id: target-1)", markdown);
    }

    [Fact]
    public async Task Unsupported_block_types_leave_a_marker_instead_of_vanishing()
    {
        var markdown = await RenderTopLevelAsync(Raw("""{"id":"x","type":"synced_thing","has_children":false,"synced_thing":{}}"""));

        Assert.Equal("[synced_thing block]", markdown);
    }

    [Fact]
    public async Task A_table_of_contents_renders_nothing()
    {
        var markdown = await RenderTopLevelAsync(
            Raw("""{"id":"toc","type":"table_of_contents","has_children":false,"table_of_contents":{}}"""),
            Paragraph("after"));

        Assert.Equal("after", markdown);
    }

    [Fact]
    public async Task An_empty_page_renders_as_an_empty_string()
    {
        Assert.Equal("", await RenderTopLevelAsync());
    }

    // ------------------------------------------------------------------ pagination and errors

    [Fact]
    public async Task Pagination_follows_the_cursor_until_the_last_page()
    {
        _handler.Respond(request =>
        {
            var hasCursor = StubHttpHandler.PathOf(request).Contains("start_cursor=next-1", StringComparison.Ordinal);
            var page = new JsonObject
            {
                ["results"] = new JsonArray(Paragraph(hasCursor ? "second" : "first")),
                ["has_more"] = !hasCursor,
            };
            if (!hasCursor)
            {
                page["next_cursor"] = "next-1";
            }

            return StubHttpHandler.Ok(page.ToJsonString());
        });

        var markdown = Normalize(await CreateRenderer().RenderAsync("root", CancellationToken.None));

        Assert.Equal("first\n\nsecond", markdown);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(_handler.Requests, r => Assert.Equal($"Bearer {Token}", r.Authorization));
        Assert.Contains("page_size=100", _handler.Requests[0].Path);
    }

    [Fact]
    public async Task An_api_error_while_reading_children_surfaces_as_a_notion_error()
    {
        _handler.Respond(_ => StubHttpHandler.NotFound());

        var error = await Assert.ThrowsAsync<NotionApiException>(() => CreateRenderer().RenderAsync("root", CancellationToken.None));

        Assert.Equal("object_not_found", error.Code);
        Assert.Equal(HttpStatusCode.NotFound, (HttpStatusCode)error.Status);
    }

    // ------------------------------------------------------------------ the block budget

    /// <summary>Serves <paramref name="total"/> paragraphs of the root block, 100 per page, linked by cursors.</summary>
    private void ServePagedParagraphs(int total)
    {
        _handler.Respond(request =>
        {
            var path = StubHttpHandler.PathOf(request);
            var marker = "start_cursor=c";
            var start = path.Contains(marker, StringComparison.Ordinal)
                ? int.Parse(path[(path.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..], System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            var end = Math.Min(start + 100, total);
            var page = new JsonObject
            {
                ["results"] = new JsonArray([.. Enumerable.Range(start, end - start).Select(i => (JsonNode)Paragraph($"para {i + 1}", id: $"b{i}"))]),
                ["has_more"] = end < total,
            };
            if (end < total)
            {
                page["next_cursor"] = $"c{end}";
            }

            return StubHttpHandler.Ok(page.ToJsonString());
        });
    }

    [Fact]
    public async Task A_page_with_more_blocks_than_the_budget_says_that_it_was_cut()
    {
        ServePagedParagraphs(1600);

        var markdown = Normalize(await CreateRenderer().RenderAsync("root", CancellationToken.None));

        Assert.Contains("para 1500", markdown);
        Assert.DoesNotContain("para 1501", markdown);
        Assert.EndsWith("[content truncated: page too long]", markdown);
    }

    [Fact]
    public async Task A_page_with_exactly_the_budget_of_blocks_is_not_marked_as_cut()
    {
        ServePagedParagraphs(1500);

        var markdown = Normalize(await CreateRenderer().RenderAsync("root", CancellationToken.None));

        Assert.Contains("para 1500", markdown);
        Assert.DoesNotContain("truncated", markdown);
    }

    [Fact]
    public async Task A_page_just_over_the_budget_is_marked_as_cut_too()
    {
        ServePagedParagraphs(1501);

        var markdown = Normalize(await CreateRenderer().RenderAsync("root", CancellationToken.None));

        Assert.DoesNotContain("para 1501", markdown);
        Assert.EndsWith("[content truncated: page too long]", markdown);
    }
}
