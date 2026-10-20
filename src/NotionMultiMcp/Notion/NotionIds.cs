using System.Text.RegularExpressions;

namespace NotionMultiMcp.Notion;

/// <summary>Extracts the id from a Notion URL, or accepts a bare id (with or without hyphens).</summary>
internal static partial class NotionIds
{
    public static string Parse(string input)
    {
        var text = input.Trim();

        // Page opened in "peek" mode inside a database: ...?v=<view>&p=<pageId>
        var peek = PeekParam().Match(text);
        if (peek.Success)
        {
            return peek.Groups[1].Value.ToLowerInvariant();
        }

        var cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        var uuid = Uuid().Match(text);
        if (uuid.Success)
        {
            return uuid.Value.ToLowerInvariant();
        }

        var matches = Hex32().Matches(text);
        if (matches.Count > 0)
        {
            return matches[^1].Value.ToLowerInvariant();
        }

        throw new ToolException($"Could not find a Notion id in '{input}'.");
    }

    /// <summary>
    /// Like <see cref="Parse"/>, but a link to a block (a page URL ending in #&lt;block id&gt;) gives the block id,
    /// not the page id, so a block link never resolves to the page that contains it.
    /// </summary>
    public static string ParseBlock(string input)
    {
        var anchor = BlockAnchor().Match(input.Trim());
        return anchor.Success ? anchor.Groups[1].Value.ToLowerInvariant() : Parse(input);
    }

    /// <summary>The 32-character form of an id returned by <see cref="Parse"/>, so ids with and without hyphens compare equal.</summary>
    public static string Compact(string id) => id.Replace("-", "", StringComparison.Ordinal);

    [GeneratedRegex("[?&]p=([0-9a-fA-F]{32})")]
    private static partial Regex PeekParam();

    [GeneratedRegex("#([0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12})$")]
    private static partial Regex BlockAnchor();

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Uuid();

    [GeneratedRegex("[0-9a-fA-F]{32}")]
    private static partial Regex Hex32();
}
