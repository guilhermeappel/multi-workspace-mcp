using System.Diagnostics;
using System.Text.Json.Nodes;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Tests;

public class RichTextTests
{
    [Fact]
    public void FromMarkdown_applies_inline_annotations()
    {
        var items = RichText.FromMarkdown("a **b** *c* `d` ~~e~~");

        Assert.True(Annotation(items, "b", "bold"));
        Assert.True(Annotation(items, "c", "italic"));
        Assert.True(Annotation(items, "d", "code"));
        Assert.True(Annotation(items, "e", "strikethrough"));
    }

    [Fact]
    public void FromMarkdown_creates_links()
    {
        var items = RichText.FromMarkdown("see [docs](https://example.com/x) here");

        var link = items.Single(i => string.Equals(Content(i), "docs", StringComparison.Ordinal));
        Assert.Equal("https://example.com/x", link!["text"]!["link"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void FromMarkdown_does_not_treat_snake_case_as_italic()
    {
        var items = RichText.FromMarkdown("use snake_case_name");

        Assert.Single(items);
        Assert.Null(items[0]!["annotations"]);
    }

    [Fact]
    public void FromPlain_splits_text_in_2000_char_chunks()
    {
        var items = RichText.FromPlain(new string('a', 4500));

        Assert.Equal(3, items.Count);
        Assert.Equal(2000, Content(items[0]).Length);
        Assert.Equal(500, Content(items[2]).Length);
    }

    [Fact]
    public void ToMarkdown_round_trips_bold_and_links()
    {
        var notion = JsonNode.Parse("""
            [
              {"type":"text","plain_text":"Hello ","annotations":{}},
              {"type":"text","plain_text":"world","annotations":{"bold":true}},
              {"type":"text","plain_text":" site","href":"https://x.dev","annotations":{}}
            ]
            """);

        Assert.Equal("Hello **world**[ site](https://x.dev)", RichText.ToMarkdown(notion));
    }

    [Fact]
    public void FromMarkdown_keeps_text_that_makes_the_patterns_backtrack_as_plain_text()
    {
        // Without a match time limit this input takes seconds and grows quadratically with its length.
        var text = string.Concat(Enumerable.Repeat("**a ", 50_000));

        var stopwatch = Stopwatch.StartNew();
        var items = RichText.FromMarkdown(text);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
        Assert.Equal(text, string.Concat(items.Select(Content)));
    }

    private static string Content(JsonNode? item) => item!["text"]!["content"]!.GetValue<string>();

    private static bool Annotation(JsonArray items, string text, string annotation)
    {
        var item = items.Single(i => Content(i) == text);
        return item!["annotations"]![annotation]!.GetValue<bool>();
    }
}
