using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NotionMultiMcp.Notion;

/// <summary>Conversion between Notion rich_text and inline Markdown.</summary>
internal static partial class RichText
{
    private const string EquationType = "equation";
    private const string MentionType = "mention";
    private const int MaxTextLength = 2000; // Notion limit per text object

    // The inline patterns can backtrack quadratically on adversarial text such as a long run of "**a ". Legitimate
    // text matches in microseconds, so these limits never affect it.
    private const int InlineMatchTimeoutMilliseconds = 100;

    [ThreadStatic]
    private static InlineBudget? _budget;

    public static string Plain(JsonNode? richText)
    {
        var sb = new StringBuilder();
        foreach (var item in J.Arr(richText))
        {
            sb.Append(J.Str(item?["plain_text"]));
        }

        return sb.ToString();
    }

    public static string ToMarkdown(JsonNode? richText)
    {
        var sb = new StringBuilder();
        foreach (var item in J.Arr(richText))
        {
            var text = J.Str(item?["plain_text"]) ?? "";
            if (text.Trim().Length == 0)
            {
                sb.Append(text);
                continue;
            }

            var annotations = item?["annotations"];
            var type = J.Str(item?["type"]);
            if (string.Equals(type, EquationType, StringComparison.Ordinal))
            {
                sb.Append('$').Append(text).Append('$');
                continue;
            }

            if (J.Bool(annotations?["code"]))
            {
                text = "`" + text + "`";
            }

            if (J.Bool(annotations?["bold"]))
            {
                text = "**" + text + "**";
            }

            if (J.Bool(annotations?["italic"]))
            {
                text = "*" + text + "*";
            }

            if (J.Bool(annotations?["strikethrough"]))
            {
                text = "~~" + text + "~~";
            }

            var href = J.Str(item?["href"]);
            if (!string.IsNullOrEmpty(href) && !string.Equals(type, MentionType, StringComparison.Ordinal))
            {
                text = $"[{text}]({href})";
            }

            sb.Append(text);
        }

        return sb.ToString();
    }

    /// <summary>Plain text -> rich_text (split into chunks of 2000 characters).</summary>
    public static JsonArray FromPlain(string text)
    {
        var array = new JsonArray();
        AddText(array, text, default, null);
        return array;
    }

    /// <summary>Inline Markdown (**bold**, *italic*, `code`, ~~strikethrough~~, [link](url)) -> rich_text.</summary>
    public static JsonArray FromMarkdown(string text)
    {
        var budget = _budget;
        if (budget is { IsExhausted: true })
        {
            return FromPlain(text);
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            return ParseInline(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Text that makes the patterns backtrack for that long is not real Markdown: keep it as it is, and stop
            // parsing the rest of the document, which would otherwise cost the same time again for every paragraph.
            budget?.Exhaust();
            return FromPlain(text);
        }
        finally
        {
            budget?.Spend(Stopwatch.GetElapsedTime(start));
        }
    }

    /// <summary>
    /// Limits the total time <see cref="FromMarkdown"/> may spend on inline patterns on this thread until the returned
    /// scope is disposed. Once it is used up, the remaining text is kept as plain text.
    /// </summary>
    public static IDisposable BeginInlineBudget(TimeSpan limit)
    {
        var previous = _budget;
        _budget = new InlineBudget(limit);
        return new BudgetScope(previous);
    }

    private static JsonArray ParseInline(string text)
    {
        var array = new JsonArray();
        var position = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            if (match.Index > position)
            {
                AddText(array, text[position..match.Index], default, null);
            }

            if (match.Groups["code"].Success)
            {
                AddText(array, match.Value[1..^1], new Style(Code: true), null);
            }
            else if (match.Groups["bold"].Success)
            {
                AddText(array, match.Value[2..^2], new Style(Bold: true), null);
            }
            else if (match.Groups["strike"].Success)
            {
                AddText(array, match.Value[2..^2], new Style(Strike: true), null);
            }
            else if (match.Groups["link"].Success)
            {
                AddText(array, match.Groups["ltext"].Value, default, match.Groups["lurl"].Value);
            }
            else
            {
                AddText(array, match.Value[1..^1], new Style(Italic: true), null);
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            AddText(array, text[position..], default, null);
        }

        return array;
    }

    private static void AddText(JsonArray array, string text, Style style, string? url)
    {
        for (var start = 0; start < text.Length; start += MaxTextLength)
        {
            var chunk = text.Substring(start, Math.Min(MaxTextLength, text.Length - start));
            var textObject = new JsonObject { ["content"] = chunk };
            if (url is not null)
            {
                textObject["link"] = new JsonObject { ["url"] = url };
            }

            var item = new JsonObject { ["type"] = "text", ["text"] = textObject };
            if (style != default)
            {
                item["annotations"] = new JsonObject
                {
                    ["bold"] = style.Bold,
                    ["italic"] = style.Italic,
                    ["strikethrough"] = style.Strike,
                    ["code"] = style.Code,
                };
            }

            array.Add(item);
        }
    }

    private sealed class InlineBudget(TimeSpan limit)
    {
        private TimeSpan _spent;
        private bool _exhausted;

        public bool IsExhausted => _exhausted || _spent >= limit;

        public void Spend(TimeSpan elapsed) => _spent += elapsed;

        public void Exhaust() => _exhausted = true;
    }

    private sealed class BudgetScope(InlineBudget? previous) : IDisposable
    {
        public void Dispose() => _budget = previous;
    }

    private readonly record struct Style(bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false);

    [GeneratedRegex(
        @"(?<code>`[^`\n]+`)" +
        @"|(?<bold>\*\*(?=\S).+?(?<=\S)\*\*)" +
        @"|(?<strike>~~(?=\S).+?(?<=\S)~~)" +
        @"|(?<link>\[(?<ltext>[^\]\n]+)\]\((?<lurl>https?://[^)\s]+)\))" +
        @"|(?<italic>(?<![\w*])\*(?=[^\s*]).+?(?<=[^\s*])\*(?![\w*])|(?<!\w)_(?=\S).+?(?<=\S)_(?!\w))",
        RegexOptions.None,
        InlineMatchTimeoutMilliseconds)]
    private static partial Regex InlinePattern();
}
