using System.Collections.Frozen;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NotionMultiMcp.Notion;

/// <summary>
/// Converts a subset of Markdown into Notion blocks: headings, paragraphs, lists
/// (with nesting), to-dos, quotes, code, dividers, images and tables.
/// </summary>
internal static partial class MarkdownToBlocks
{
    // The API accepts at most 2 levels of children per request.
    private const int MaxNesting = 2;

    // Inline parsing can backtrack quadratically on adversarial text, and each line gets its own time limit. Capping the
    // whole input keeps the worst case to a few seconds. Real documents are far smaller.
    internal const int MaxMarkdownLength = 100_000;

    // The most time one conversion may spend on inline patterns before the rest of the text stays plain.
    private static readonly TimeSpan _inlineParseBudget = TimeSpan.FromSeconds(1);

    private static readonly FrozenDictionary<string, string> _languageAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "c#",
        ["csharp"] = "c#",
        ["c#"] = "c#",
        ["js"] = "javascript",
        ["javascript"] = "javascript",
        ["ts"] = "typescript",
        ["typescript"] = "typescript",
        ["py"] = "python",
        ["python"] = "python",
        ["sh"] = "shell",
        ["shell"] = "shell",
        ["bash"] = "bash",
        ["zsh"] = "shell",
        ["ps1"] = "powershell",
        ["pwsh"] = "powershell",
        ["powershell"] = "powershell",
        ["json"] = "json",
        ["yml"] = "yaml",
        ["yaml"] = "yaml",
        ["md"] = "markdown",
        ["markdown"] = "markdown",
        ["sql"] = "sql",
        ["html"] = "html",
        ["xml"] = "xml",
        ["css"] = "css",
        ["scss"] = "scss",
        ["java"] = "java",
        ["kotlin"] = "kotlin",
        ["go"] = "go",
        ["rust"] = "rust",
        ["c"] = "c",
        ["cpp"] = "c++",
        ["c++"] = "c++",
        ["php"] = "php",
        ["ruby"] = "ruby",
        ["swift"] = "swift",
        ["dart"] = "dart",
        ["dockerfile"] = "docker",
        ["docker"] = "docker",
        ["graphql"] = "graphql",
        ["mermaid"] = "mermaid",
        ["diff"] = "diff",
        ["r"] = "r",
        ["scala"] = "scala",
        ["lua"] = "lua",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static List<JsonObject> Convert(string markdown)
    {
        if (markdown.Length > MaxMarkdownLength)
        {
            throw new ToolException($"The content is {markdown.Length:N0} characters and the limit is {MaxMarkdownLength:N0}. Send long content in several append_content calls.");
        }

        using var budgetScope = RichText.BeginInlineBudget(_inlineParseBudget);

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<JsonObject>();
        var listStack = new List<(int Indent, JsonObject Block)>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(TextBlock(BlockTypes.Paragraph, string.Join("\n", paragraph)));
                paragraph.Clear();
            }
        }

        void AddTopLevel(JsonObject block)
        {
            FlushParagraph();
            listStack.Clear();
            blocks.Add(block);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var language = line[3..].Trim();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }

                AddTopLevel(CodeBlock(string.Join("\n", code), language));
                continue;
            }

            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (line.StartsWith('|'))
            {
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                {
                    rows.Add(lines[i].Trim());
                }

                i--;
                AddTopLevel(TableBlock(rows));
                continue;
            }

            if (Divider().IsMatch(line))
            {
                AddTopLevel(new JsonObject { ["object"] = "block", ["type"] = BlockTypes.Divider, [BlockTypes.Divider] = new JsonObject() });
                continue;
            }

            if (TryListItem(line) is { } item)
            {
                FlushParagraph();
                var indent = raw.Length - raw.TrimStart().Length;
                while (listStack.Count > 0 && listStack[^1].Indent >= indent)
                {
                    listStack.RemoveAt(listStack.Count - 1);
                }

                if (listStack.Count == 0)
                {
                    blocks.Add(item);
                }
                else
                {
                    var parentIndex = Math.Min(listStack.Count - 1, MaxNesting - 1);
                    AddChild(listStack[parentIndex].Block, item);
                    listStack.RemoveRange(parentIndex + 1, listStack.Count - parentIndex - 1);
                }

                listStack.Add((indent, item));
                continue;
            }

            var heading = Heading().Match(line);
            if (heading.Success)
            {
                var level = Math.Min(heading.Groups[1].Value.Length, 3);
                AddTopLevel(TextBlock(BlockTypes.Heading(level), heading.Groups[2].Value));
                continue;
            }

            if (line.StartsWith('>'))
            {
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('>'); i++)
                {
                    quote.Add(lines[i].Trim()[1..].TrimStart());
                }

                i--;
                AddTopLevel(TextBlock(BlockTypes.Quote, string.Join("\n", quote)));
                continue;
            }

            var image = Image().Match(line);
            if (image.Success)
            {
                AddTopLevel(new JsonObject
                {
                    ["object"] = "block",
                    ["type"] = BlockTypes.Image,
                    [BlockTypes.Image] = new JsonObject
                    {
                        ["type"] = "external",
                        ["external"] = new JsonObject { ["url"] = image.Groups[2].Value },
                        ["caption"] = RichText.FromPlain(image.Groups[1].Value),
                    },
                });
                continue;
            }

            listStack.Clear();
            paragraph.Add(line);
        }

        FlushParagraph();
        return blocks;
    }

    private static JsonObject? TryListItem(string line)
    {
        var todo = TodoItem().Match(line);
        if (todo.Success)
        {
            var block = TextBlock(BlockTypes.ToDo, todo.Groups[2].Value);
            block[BlockTypes.ToDo]!["checked"] = todo.Groups[1].Value is "x" or "X";
            return block;
        }

        var bullet = BulletItem().Match(line);
        if (bullet.Success)
        {
            return TextBlock(BlockTypes.BulletedListItem, bullet.Groups[1].Value);
        }

        var numbered = NumberedItem().Match(line);
        return numbered.Success ? TextBlock(BlockTypes.NumberedListItem, numbered.Groups[1].Value) : null;
    }

    private static void AddChild(JsonObject parent, JsonObject child)
    {
        var type = J.Str(parent["type"])!;
        var data = (JsonObject)parent[type]!;
        if (data["children"] is not JsonArray children)
        {
            children = [];
            data["children"] = children;
        }

        children.Add(child);
    }

    private static JsonObject TextBlock(string type, string markdown) => new()
    {
        ["object"] = "block",
        ["type"] = type,
        [type] = new JsonObject { ["rich_text"] = RichText.FromMarkdown(markdown) },
    };

    private static JsonObject CodeBlock(string code, string language) => new()
    {
        ["object"] = "block",
        ["type"] = BlockTypes.Code,
        [BlockTypes.Code] = new JsonObject
        {
            ["rich_text"] = RichText.FromPlain(code),
            ["language"] = _languageAliases.TryGetValue(language, out var mapped) ? mapped : "plain text",
        },
    };

    private static JsonObject TableBlock(List<string> rawRows)
    {
        var hasHeader = rawRows.Count > 1 && TableSeparator().IsMatch(rawRows[1]);
        var rows = rawRows
            .Where(r => !TableSeparator().IsMatch(r))
            .Select(r => r.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .ToList();
        if (rows.Count == 0)
        {
            return TextBlock(BlockTypes.Paragraph, string.Join("\n", rawRows));
        }

        var width = rows.Max(r => r.Length);

        var children = new JsonArray();
        foreach (var row in rows)
        {
            var cells = new JsonArray();
            for (var c = 0; c < width; c++)
            {
                cells.Add(RichText.FromMarkdown(c < row.Length ? row[c] : ""));
            }

            children.Add(new JsonObject
            {
                ["object"] = "block",
                ["type"] = BlockTypes.TableRow,
                [BlockTypes.TableRow] = new JsonObject { ["cells"] = cells },
            });
        }

        return new JsonObject
        {
            ["object"] = "block",
            ["type"] = BlockTypes.Table,
            [BlockTypes.Table] = new JsonObject
            {
                ["table_width"] = width,
                ["has_column_header"] = hasHeader,
                ["has_row_header"] = false,
                ["children"] = children,
            },
        };
    }

    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex Divider();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[-*+]\s+\[([ xX])\]\s*(.*)$")]
    private static partial Regex TodoItem();

    [GeneratedRegex(@"^[-*+]\s+(.*)$")]
    private static partial Regex BulletItem();

    [GeneratedRegex(@"^\d+[.)]\s+(.*)$")]
    private static partial Regex NumberedItem();

    [GeneratedRegex(@"^!\[([^\]]*)\]\((https?://\S+)\)$")]
    private static partial Regex Image();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparator();
}
