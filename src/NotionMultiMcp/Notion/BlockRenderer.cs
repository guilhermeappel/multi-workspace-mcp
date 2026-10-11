using System.Text;
using System.Text.Json.Nodes;

namespace NotionMultiMcp.Notion;

/// <summary>Reads the blocks of a page (recursively) and returns Markdown.</summary>
internal sealed class BlockRenderer(NotionClient notion, string token)
{
    private const int MaxDepth = 4;
    private const int BlockBudget = 1500;
    private const int PageSize = 100;

    private int _remainingBlocks = BlockBudget;

    public bool Truncated { get; private set; }

    public async Task<string> RenderAsync(string blockId, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        await RenderChildrenAsync(blockId, 0, sb, cancellationToken);
        if (Truncated)
        {
            sb.AppendLine().AppendLine("[content truncated: page too long]");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task RenderChildrenAsync(string blockId, int depth, StringBuilder sb, CancellationToken cancellationToken)
    {
        var numbered = 0;
        foreach (var block in await ListChildrenAsync(blockId, cancellationToken))
        {
            if (_remainingBlocks-- <= 0)
            {
                Truncated = true;
                return;
            }

            var type = J.Str(block["type"]) ?? "unsupported";
            numbered = string.Equals(type, BlockTypes.NumberedListItem, StringComparison.Ordinal) ? numbered + 1 : 0;
            var indent = new string(' ', depth * 2);

            if (string.Equals(type, BlockTypes.Table, StringComparison.Ordinal))
            {
                await RenderTableAsync(block, indent, sb, cancellationToken);
                continue;
            }

            var rendered = RenderBlock(block, type, numbered);
            if (rendered.Text is not null)
            {
                foreach (var line in rendered.Text.Split('\n'))
                {
                    sb.Append(indent).AppendLine(line);
                }

                if (rendered.BlankLineAfter)
                {
                    sb.AppendLine();
                }
            }

            if (rendered.Recurse && J.Bool(block["has_children"]) && depth < MaxDepth && J.Str(block["id"]) is { } childId)
            {
                await RenderChildrenAsync(childId, rendered.Flatten ? depth : depth + 1, sb, cancellationToken);
            }
        }
    }

    private async Task<List<JsonObject>> ListChildrenAsync(string blockId, CancellationToken cancellationToken)
    {
        var result = new List<JsonObject>();
        var cursor = default(string);
        do
        {
            var path = $"blocks/{blockId}/children?page_size={PageSize}"
                + (cursor is null ? "" : "&start_cursor=" + Uri.EscapeDataString(cursor));
            var page = await notion.GetAsync(token, path, cancellationToken);
            result.AddRange(J.Arr(page["results"]).OfType<JsonObject>());
            cursor = J.Bool(page["has_more"]) ? J.Str(page["next_cursor"]) : null;
        }
        while (cursor is not null && result.Count < _remainingBlocks);

        // The budget stopped the reading while Notion still had more blocks: say so instead of cutting silently.
        if (cursor is not null)
        {
            Truncated = true;
        }

        return result;
    }

    private async Task RenderTableAsync(JsonObject table, string indent, StringBuilder sb, CancellationToken cancellationToken)
    {
        var id = J.Str(table["id"]);
        if (id is null)
        {
            return;
        }

        var hasHeader = J.Bool(table[BlockTypes.Table]?["has_column_header"]);
        var rows = await ListChildrenAsync(id, cancellationToken);
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = J.Arr(rows[r][BlockTypes.TableRow]?["cells"])
                .Select(cell => RichText.ToMarkdown(cell).Replace("|", "\\|").Replace("\n", " "))
                .ToList();
            sb.Append(indent).Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
            if (r == 0)
            {
                // Markdown requires a separator line after the first row, with or without a header.
                sb.Append(indent).Append('|').Append(string.Concat(cells.Select(_ => " --- |"))).AppendLine();
                if (!hasHeader)
                {
                    sb.Append(indent).AppendLine("<!-- first row is not a header -->");
                }
            }
        }

        _remainingBlocks -= rows.Count;
        sb.AppendLine();
    }

    private static Rendered RenderBlock(JsonObject block, string type, int number)
    {
        var data = block[type];
        var id = J.Str(block["id"]);
        string Text() => RichText.ToMarkdown(data?["rich_text"]);

        return type switch
        {
            BlockTypes.Paragraph => new(Text(), BlankLineAfter: true),
            BlockTypes.Heading1 => new("# " + Text(), BlankLineAfter: true),
            BlockTypes.Heading2 => new("## " + Text(), BlankLineAfter: true),
            BlockTypes.Heading3 => new("### " + Text(), BlankLineAfter: true),
            BlockTypes.BulletedListItem => new("- " + Text()),
            BlockTypes.NumberedListItem => new($"{number}. " + Text()),
            BlockTypes.ToDo => new((J.Bool(data?["checked"]) ? "- [x] " : "- [ ] ") + Text()),
            BlockTypes.Toggle => new("- ▸ " + Text()),
            BlockTypes.Quote => new("> " + Text().Replace("\n", "\n> "), BlankLineAfter: true),
            BlockTypes.Callout => new("> " + (J.Str(data?["icon"]?["emoji"]) is { } emoji ? emoji + " " : "") + Text().Replace("\n", "\n> "), BlankLineAfter: true),
            BlockTypes.Code => new($"```{J.Str(data?["language"])}\n{RichText.Plain(data?["rich_text"])}\n```", BlankLineAfter: true),
            BlockTypes.Equation => new($"$${J.Str(data?["expression"])}$$", BlankLineAfter: true),
            BlockTypes.Divider => new("---", BlankLineAfter: true),
            BlockTypes.ChildPage => new($"[child page] {J.Str(data?["title"])} (id: {id})", Recurse: false),
            BlockTypes.ChildDatabase => new($"[child database] {J.Str(data?["title"])} (id: {id}) — use fetch_data_source", Recurse: false),
            BlockTypes.Image or BlockTypes.Video or BlockTypes.File or BlockTypes.Pdf or BlockTypes.Audio => new($"[{type}: {FileLabel(data)}]({FileUrl(data)})", Recurse: false),
            BlockTypes.Bookmark or BlockTypes.Embed or BlockTypes.LinkPreview => new($"[{type}]({J.Str(data?["url"])})", Recurse: false),
            BlockTypes.LinkToPage => new($"[link to page] (id: {J.Str(data?["page_id"]) ?? J.Str(data?["database_id"])})", Recurse: false),
            BlockTypes.ColumnList or BlockTypes.Column or BlockTypes.SyncedBlock => new(null, Flatten: true),
            BlockTypes.TableOfContents or BlockTypes.Breadcrumb => new(null, Recurse: false),
            _ => new($"[{type} block]", Recurse: false),
        };
    }

    private static string FileLabel(JsonNode? data)
    {
        var caption = RichText.Plain(data?["caption"]);
        return caption.Length > 0 ? caption : J.Str(data?["name"]) ?? "file";
    }

    private static string? FileUrl(JsonNode? data)
    {
        var kind = J.Str(data?["type"]);
        return kind is null ? null : J.Str(data?[kind]?["url"]);
    }

    private readonly record struct Rendered(string? Text, bool Recurse = true, bool Flatten = false, bool BlankLineAfter = false);
}
