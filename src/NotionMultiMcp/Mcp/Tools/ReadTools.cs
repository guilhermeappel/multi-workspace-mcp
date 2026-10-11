using System.Text;
using System.Text.Json.Nodes;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Mcp.Tools;

/// <summary>The tools that only read from Notion.</summary>
internal sealed class ReadTools(ToolContext context, DataSourceResolver dataSources)
{
    public async Task<string> ListWorkspacesAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder("Configured workspaces:\n");
        foreach (var name in context.Workspaces.Names)
        {
            try
            {
                var me = await context.Notion.GetAsync(context.Workspaces.TokenFor(name), "users/me", cancellationToken);
                var notionWorkspace = J.Str(me["bot"]?["workspace_name"]) ?? "?";
                sb.AppendLine($"- {name}: Notion workspace \"{notionWorkspace}\" (integration \"{J.Str(me["name"])}\")");
            }
            catch (NotionApiException e)
            {
                sb.AppendLine($"- {name}: ERROR {e.ToToolMessage()}");
            }
        }

        return sb.ToString();
    }

    public async Task<string> SearchAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var token = context.Token(args);
        var body = new JsonObject
        {
            ["page_size"] = ToolArguments.Limit(args, ToolArguments.DefaultSearchPageSize),
            ["sort"] = new JsonObject { ["direction"] = "descending", ["timestamp"] = "last_edited_time" },
        };

        if (J.Str(args["query"]) is { Length: > 0 } query)
        {
            body["query"] = query;
        }

        if (J.Str(args["type"]) is NotionObjectTypes.Page or NotionObjectTypes.DataSource)
        {
            body["filter"] = new JsonObject { ["property"] = "object", ["value"] = J.Str(args["type"]) };
        }

        if (J.Str(args["cursor"]) is { Length: > 0 } cursor)
        {
            body["start_cursor"] = cursor;
        }

        var response = await context.Notion.PostReadOnlyAsync(token, "search", body, cancellationToken);
        var results = J.Arr(response["results"]);
        if (results.Count == 0)
        {
            return "No results. Note: the integration only sees pages and databases explicitly shared with it.";
        }

        var sb = new StringBuilder();
        foreach (var item in results)
        {
            var kind = J.Str(item?["object"]);
            var title = string.Equals(kind, NotionObjectTypes.Page, StringComparison.Ordinal)
                ? Properties.PageTitle(item)
                : ToolText.Fallback(RichText.Plain(item?["title"]));
            sb.AppendLine($"- [{kind}] {title} | id: {J.Str(item?["id"])} | edited: {J.Str(item?["last_edited_time"])} | {J.Str(item?["url"])}");
        }

        ToolText.AppendCursor(sb, response);
        return sb.ToString();
    }

    public async Task<string> FetchPageAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var token = context.Token(args);
        var pageId = NotionIds.Parse(ToolArguments.Required(args, "page_id"));
        var page = await context.Notion.GetAsync(token, $"pages/{pageId}", cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"# {Properties.PageTitle(page)}");
        sb.AppendLine($"id: {J.Str(page["id"])}");
        sb.AppendLine($"url: {J.Str(page["url"])}");
        sb.AppendLine($"last edited: {J.Str(page["last_edited_time"])}");
        if (J.Bool(page["in_trash"]))
        {
            sb.AppendLine("status: in trash");
        }

        var parent = page["parent"];
        var parentType = J.Str(parent?["type"]);
        if (parentType is not null && J.Str(parent?[parentType]) is { } parentId)
        {
            sb.AppendLine($"parent: {parentType} {parentId}");
        }

        var lines = Properties.Readable(page["properties"]).ToList();
        if (lines.Count > 0)
        {
            sb.AppendLine().AppendLine("Properties:");
            foreach (var (name, value) in lines)
            {
                sb.AppendLine($"- {name}: {value}");
            }
        }

        if (J.BoolOrNull(args["include_content"]) ?? true)
        {
            var content = await new BlockRenderer(context.Notion, token).RenderAsync(pageId, cancellationToken);
            sb.AppendLine().AppendLine("---").AppendLine();
            sb.AppendLine(content.Length > 0 ? content : "(empty page)");
        }

        return sb.ToString();
    }

    public async Task<string> FetchDataSourceAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var token = context.Token(args);
        var dataSource = await dataSources.ResolveAsync(token, ToolArguments.Required(args, "data_source_id"), cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"# {ToolText.Fallback(RichText.Plain(dataSource["title"]))}");
        sb.AppendLine($"data_source_id: {J.Str(dataSource["id"])}");
        if (J.Str(dataSource["parent"]?["database_id"]) is { } databaseId)
        {
            sb.AppendLine($"database_id: {databaseId}");
        }

        sb.AppendLine().AppendLine("Properties:");
        foreach (var (name, property) in dataSource["properties"] as JsonObject ?? [])
        {
            var type = J.Str(property?["type"]) ?? "?";
            var line = $"- {name} ({type})";
            if (type is PropertyTypes.Select or PropertyTypes.MultiSelect or PropertyTypes.Status)
            {
                var options = J.Arr(property?[type]?["options"]).Select(o => J.Str(o?["name"])).ToList();
                if (options.Count > 0)
                {
                    line += ": " + string.Join(" | ", options);
                }
            }
            else if (string.Equals(type, PropertyTypes.Relation, StringComparison.Ordinal) && J.Str(property?["relation"]?["data_source_id"]) is { } related)
            {
                line += $" → data source {related}";
            }

            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    public async Task<string> QueryDataSourceAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var token = context.Token(args);
        var dataSource = await dataSources.ResolveAsync(token, ToolArguments.Required(args, "data_source_id"), cancellationToken);
        var dataSourceId = J.Str(dataSource["id"])!;

        var body = new JsonObject { ["page_size"] = ToolArguments.Limit(args, ToolArguments.DefaultQueryPageSize) };
        if (J.ObjectArg(args["filter"]) is { Count: > 0 } filter)
        {
            body["filter"] = filter;
        }

        if (J.ArrayArg(args["sorts"]) is { Count: > 0 } sorts)
        {
            body["sorts"] = sorts;
        }

        if (J.Str(args["cursor"]) is { Length: > 0 } cursor)
        {
            body["start_cursor"] = cursor;
        }

        var response = await context.Notion.PostReadOnlyAsync(token, $"data_sources/{dataSourceId}/query", body, cancellationToken);
        var results = J.Arr(response["results"]);

        var sb = new StringBuilder();
        sb.AppendLine($"{ToolText.Fallback(RichText.Plain(dataSource["title"]))} (data_source_id: {dataSourceId}): {results.Count} row(s)");
        foreach (var row in results)
        {
            var values = Properties.Readable(row?["properties"]).Select(p => $"{p.Name}: {p.Value}");
            sb.AppendLine($"- **{Properties.PageTitle(row)}** (id: {J.Str(row?["id"])}) — {string.Join("; ", values)}");
        }

        ToolText.AppendCursor(sb, response);
        return sb.ToString();
    }
}
