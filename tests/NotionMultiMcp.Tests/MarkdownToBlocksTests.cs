using System.Text.Json.Nodes;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Tests;

public class MarkdownToBlocksTests
{
    [Fact]
    public void Converts_headings_paragraphs_and_dividers()
    {
        var blocks = MarkdownToBlocks.Convert("# Title\n\nline 1\nline 2\n\n---\n#### small");

        Assert.Equal(new[] { "heading_1", "paragraph", "divider", "heading_3" }, blocks.Select(Type));
        Assert.Equal("line 1\nline 2", PlainText(blocks[1]));
    }

    [Fact]
    public void Nests_list_items_by_indentation()
    {
        var blocks = MarkdownToBlocks.Convert("- pai\n  - filho\n    - neto\n- outro");

        Assert.Equal(2, blocks.Count);
        var child = blocks[0]["bulleted_list_item"]!["children"]![0]!;
        Assert.Equal("filho", PlainText(child));
        Assert.Equal("neto", PlainText(child["bulleted_list_item"]!["children"]![0]));
    }

    [Fact]
    public void Limits_nesting_to_two_levels()
    {
        var blocks = MarkdownToBlocks.Convert("- a\n  - b\n    - c\n      - d");

        var b = blocks[0]["bulleted_list_item"]!["children"]![0]!;
        var grandchildren = b["bulleted_list_item"]!["children"]!.AsArray();
        Assert.Equal(new[] { "c", "d" }, grandchildren.Select(PlainText));
    }

    [Fact]
    public void Converts_todos_and_numbered_items()
    {
        var blocks = MarkdownToBlocks.Convert("- [x] feito\n- [ ] pendente\n1. primeiro\n2) segundo");

        Assert.Equal(new[] { "to_do", "to_do", "numbered_list_item", "numbered_list_item" }, blocks.Select(Type));
        Assert.True(blocks[0]["to_do"]!["checked"]!.GetValue<bool>());
        Assert.False(blocks[1]["to_do"]!["checked"]!.GetValue<bool>());
    }

    [Fact]
    public void Converts_code_fences_with_language_aliases()
    {
        var blocks = MarkdownToBlocks.Convert("```cs\nvar x = 1;\n# not a heading\n```");

        var code = Assert.Single(blocks);
        Assert.Equal("c#", code["code"]!["language"]!.GetValue<string>());
        Assert.Equal("var x = 1;\n# not a heading", PlainText(code));
    }

    [Fact]
    public void Unknown_code_language_becomes_plain_text()
    {
        var block = Assert.Single(MarkdownToBlocks.Convert("```cobol-99\nx\n```"));
        Assert.Equal("plain text", block["code"]!["language"]!.GetValue<string>());
    }

    [Fact]
    public void Converts_tables_with_header()
    {
        var block = Assert.Single(MarkdownToBlocks.Convert("| A | B |\n|---|:---:|\n| 1 | 2 |\n| 3 |"));

        var table = block["table"]!;
        Assert.Equal(2, table["table_width"]!.GetValue<int>());
        Assert.True(table["has_column_header"]!.GetValue<bool>());
        var rows = table["children"]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows[2]!["table_row"]!["cells"]!.AsArray().Count);
    }

    [Fact]
    public void Merges_consecutive_quote_lines()
    {
        var block = Assert.Single(MarkdownToBlocks.Convert("> um\n> dois"));
        Assert.Equal("quote", Type(block));
        Assert.Equal("um\ndois", PlainText(block));
    }

    [Fact]
    public void Converts_images_by_url()
    {
        var block = Assert.Single(MarkdownToBlocks.Convert("![logo](https://x.dev/a.png)"));
        Assert.Equal("https://x.dev/a.png", block["image"]!["external"]!["url"]!.GetValue<string>());
    }

    private static string Type(JsonNode? block) => block!["type"]!.GetValue<string>();

    private static string PlainText(JsonNode? block)
    {
        var type = Type(block);
        return string.Concat(block![type]!["rich_text"]!.AsArray()
            .Select(t => t!["text"]!["content"]!.GetValue<string>()));
    }
}
