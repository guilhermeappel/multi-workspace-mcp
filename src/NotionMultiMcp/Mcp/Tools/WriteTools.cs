using System.Text.Json.Nodes;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Mcp.Tools;

/// <summary>The tools that change Notion. Writes are never retried after a server error (see NotionClient).</summary>
internal sealed class WriteTools(ToolContext context, DataSourceResolver dataSources)
{
    private const int MaxChildrenPerRequest = 100;

    public async Task<string> CreatePageAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var token = context.Token(args);
        var parentPage = J.Str(args["parent_page_id"]);
        var parentDataSource = J.Str(args["parent_data_source_id"]);
        var title = J.Str(args["title"]);
        var propertiesInput = J.ObjectArg(args["properties"]);

        if (string.IsNullOrWhiteSpace(parentPage) == string.IsNullOrWhiteSpace(parentDataSource))
        {
            throw new ToolException("Pass exactly one of parent_page_id or parent_data_source_id.");
        }

        var target = await BuildPageTargetAsync(token, parentPage, parentDataSource, title, propertiesInput, cancellationToken);

        var blocks = MarkdownToBlocks.Convert(J.Str(args["content_markdown"]) ?? "");
        var body = new JsonObject
        {
            ["parent"] = target.Parent,
            ["properties"] = target.Properties,
            ["children"] = new JsonArray(blocks.Take(MaxChildrenPerRequest).Cast<JsonNode>().ToArray()),
        };

        var page = await context.Notion.PostAsync(token, "pages", body, cancellationToken);
        var pageId = J.Str(page["id"])!;
        var url = J.Str(page["url"]);

        var appended = await AppendBlocksAsync(token, pageId, [.. blocks.Skip(MaxChildrenPerRequest)], cancellationToken);
        if (appended.Failure is { } failure)
        {
            // The page exists. A plain error would make the model create it again, which is the duplicate
            // that writes are never retried to avoid, so say what exists and what is missing.
            var landed = Math.Min(blocks.Count, MaxChildrenPerRequest) + appended.Landed;
            throw new ToolException(
                $"The page was created (id: {pageId}, url: {url}) but only {landed} of {blocks.Count} blocks were added: " +
                $"{failure.ToToolMessage()}\nDo not create the page again: add the remaining content with append_content on this page id.");
        }

        return $"Created page \"{Properties.PageTitle(page)}\"\nid: {pageId}\nurl: {url}";
    }

    public async Task<string> AppendContentAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var token = context.Token(args);
        var pageId = NotionIds.Parse(ToolArguments.Required(args, "page_id"));
        var blocks = MarkdownToBlocks.Convert(ToolArguments.Required(args, "content_markdown"));
        if (blocks.Count == 0)
        {
            throw new ToolException("content_markdown produced no blocks.");
        }

        var appended = await AppendBlocksAsync(token, pageId, blocks, cancellationToken);
        if (appended.Failure is { } failure)
        {
            if (appended.Landed == 0)
            {
                throw failure;
            }

            throw new ToolException(
                $"Appending stopped part way: only {appended.Landed} of {blocks.Count} blocks reached page {pageId}. " +
                $"{failure.ToToolMessage()}\nDo not repeat the whole call: the first {appended.Landed} blocks are already on the page.");
        }

        return $"Appended {blocks.Count} block(s) to page {pageId}.";
    }

    public async Task<string> UpdatePageAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var token = context.Token(args);
        var pageId = NotionIds.Parse(ToolArguments.Required(args, "page_id"));
        var title = J.Str(args["title"]);
        var propertiesInput = J.ObjectArg(args["properties"]);
        var inTrash = J.BoolOrNull(args["in_trash"]);

        if (title is null && propertiesInput is null && inTrash is null)
        {
            throw new ToolException("Nothing to update: pass title, properties and/or in_trash.");
        }

        var body = new JsonObject();
        if (title is not null || propertiesInput is not null)
        {
            var current = await context.Notion.GetAsync(token, $"pages/{pageId}", cancellationToken);
            var schema = Properties.SchemaTypes(current["properties"]);
            var properties = Properties.Build(schema, propertiesInput);
            if (title is not null)
            {
                properties[Properties.TitlePropertyName(schema)] = Properties.TitleValue(title);
            }

            body["properties"] = properties;
        }

        if (inTrash is { } trash)
        {
            body["in_trash"] = trash;
        }

        var page = await context.Notion.PatchAsync(token, $"pages/{pageId}", body, cancellationToken);
        var status = inTrash switch { true => " (moved to trash)", false => " (restored)", null => "" };
        return $"Updated page \"{Properties.PageTitle(page)}\"{status}\nid: {pageId}\nurl: {J.Str(page["url"])}";
    }

    /// <summary>Builds the parent and the properties of a new page, for a data source row or a regular page.</summary>
    private async Task<PageTarget> BuildPageTargetAsync(
        string token,
        string? parentPage,
        string? parentDataSource,
        string? title,
        JsonObject? propertiesInput,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(parentDataSource))
        {
            var dataSource = await dataSources.ResolveAsync(token, parentDataSource, cancellationToken);
            var schema = Properties.SchemaTypes(dataSource["properties"]);
            var properties = Properties.Build(schema, propertiesInput);
            if (title is not null)
            {
                properties[Properties.TitlePropertyName(schema)] = Properties.TitleValue(title);
            }

            var dataSourceParent = new JsonObject { ["type"] = "data_source_id", ["data_source_id"] = J.Str(dataSource["id"]) };
            return new PageTarget(dataSourceParent, properties);
        }

        if (propertiesInput is { Count: > 0 })
        {
            throw new ToolException("'properties' only applies when the parent is a data source. Use title/content for regular pages.");
        }

        var pageParent = new JsonObject { ["type"] = "page_id", ["page_id"] = NotionIds.Parse(parentPage!) };
        var pageProperties = new JsonObject { ["title"] = Properties.TitleValue(title ?? "Untitled") };
        return new PageTarget(pageParent, pageProperties);
    }

    /// <summary>
    /// Appends blocks in batches of at most 100. A failure is returned together with how many blocks already
    /// landed, because the caller has to tell the model what exists before it decides what to do next.
    /// </summary>
    private async Task<AppendOutcome> AppendBlocksAsync(string token, string blockId, List<JsonObject> blocks, CancellationToken cancellationToken)
    {
        var landed = 0;
        foreach (var chunk in blocks.Chunk(MaxChildrenPerRequest))
        {
            var body = new JsonObject { ["children"] = new JsonArray(chunk.Cast<JsonNode>().ToArray()) };
            try
            {
                await context.Notion.PatchAsync(token, $"blocks/{blockId}/children", body, cancellationToken);
            }
            catch (NotionApiException e)
            {
                return new AppendOutcome(landed, e);
            }

            landed += chunk.Length;
        }

        return new AppendOutcome(landed, null);
    }

    private sealed record AppendOutcome(int Landed, NotionApiException? Failure);

    private sealed record PageTarget(JsonObject Parent, JsonObject Properties);
}
