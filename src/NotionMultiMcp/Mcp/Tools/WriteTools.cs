using System.Text.Json.Nodes;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Mcp.Tools;

/// <summary>The tools that change Notion. Writes are never retried after a server error (see NotionClient).</summary>
internal sealed class WriteTools(ToolContext context, DataSourceResolver dataSources)
{
    private const int MaxChildrenPerRequest = 100;
    private const int BlockPreviewLength = 100;
    private const int MaxNestedBlocksChecked = 1000;
    private const int ChildrenPageSize = 100;

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

    public async Task<string> MovePageAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var token = context.Token(args);
        var pageId = NotionIds.Parse(ToolArguments.Required(args, "page_id"));
        var newParentPage = J.Str(args["new_parent_page_id"]);
        var newParentDataSource = J.Str(args["new_parent_data_source_id"]);

        if (string.IsNullOrWhiteSpace(newParentPage) == string.IsNullOrWhiteSpace(newParentDataSource))
        {
            throw new ToolException("Pass exactly one of new_parent_page_id or new_parent_data_source_id.");
        }

        var target = await BuildMoveTargetAsync(token, pageId, newParentPage, newParentDataSource, cancellationToken);
        var page = await context.Notion.PostAsync(token, $"pages/{pageId}/move", new JsonObject { ["parent"] = target.Parent }, cancellationToken);

        // Notion may answer with a partial page (only object and id), so the title and url are optional.
        var url = J.Str(page["url"]);
        var title = page["properties"] is null ? "" : $" \"{Properties.PageTitle(page)}\"";
        return $"Moved page{title} under {target.Destination}\nid: {pageId}" + (url is null ? "" : $"\nurl: {url}");
    }

    public async Task<string> DeleteBlockAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var token = context.Token(args);
        var blockId = NotionIds.ParseBlock(ToolArguments.Required(args, "block_id"));

        // DELETE also trashes pages and databases. Read the block first so this tool stays scoped to content blocks.
        var block = await context.Notion.GetAsync(token, $"blocks/{blockId}", cancellationToken);
        var type = J.Str(block["type"]) ?? "unknown";
        if (string.Equals(type, BlockTypes.ChildPage, StringComparison.Ordinal))
        {
            throw new ToolException("This id is a page, not a content block. To trash a page, use update_page with in_trash: true.");
        }

        if (string.Equals(type, BlockTypes.ChildDatabase, StringComparison.Ordinal))
        {
            throw new ToolException("This id is a database, not a content block. Deleting databases is not supported by this server.");
        }

        var preview = BlockPreview(block, type);
        if (J.Bool(block["in_trash"]))
        {
            return $"The {type} block{preview} is already in the trash\nid: {blockId}";
        }

        // The nested blocks go to the trash with it, and a toggle or a column can hold a whole subpage or database.
        if (J.Bool(block["has_children"]))
        {
            await RefuseNestedPagesAsync(token, blockId, cancellationToken);
        }

        await context.Notion.DeleteAsync(token, $"blocks/{blockId}", cancellationToken);
        return $"Moved the {type} block{preview} to the trash\nid: {blockId}";
    }

    /// <summary>Walks the blocks nested under a block and refuses the delete if a page or database is among them.</summary>
    private async Task RefuseNestedPagesAsync(string token, string blockId, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>([blockId]);
        var checkedBlocks = 0;
        while (pending.TryPop(out var parentId))
        {
            var cursor = default(string);
            do
            {
                var path = $"blocks/{parentId}/children?page_size={ChildrenPageSize}"
                    + (cursor is null ? "" : "&start_cursor=" + Uri.EscapeDataString(cursor));
                var page = await context.Notion.GetAsync(token, path, cancellationToken);
                foreach (var child in J.Arr(page["results"]).OfType<JsonObject>())
                {
                    var childType = J.Str(child["type"]);
                    if (string.Equals(childType, BlockTypes.ChildPage, StringComparison.Ordinal)
                        || string.Equals(childType, BlockTypes.ChildDatabase, StringComparison.Ordinal))
                    {
                        var kind = string.Equals(childType, BlockTypes.ChildPage, StringComparison.Ordinal) ? "page" : "database";
                        throw new ToolException(
                            $"This block contains the {kind} \"{J.Str(child[childType!]?["title"])}\" (id: {J.Str(child["id"])}), which would go to " +
                            "the trash with it. Move the page out first with move_page, or delete the other blocks one by one.");
                    }

                    if (++checkedBlocks > MaxNestedBlocksChecked)
                    {
                        throw new ToolException(
                            $"This block has more than {MaxNestedBlocksChecked} nested blocks, too many to check for subpages. " +
                            "Delete the blocks inside it in smaller parts.");
                    }

                    if (J.Bool(child["has_children"]) && J.Str(child["id"]) is { } childId)
                    {
                        pending.Push(childId);
                    }
                }

                cursor = J.Bool(page["has_more"]) ? J.Str(page["next_cursor"]) : null;
            }
            while (cursor is not null);
        }
    }

    private static string BlockPreview(JsonObject block, string type)
    {
        var text = RichText.Plain(block[type]?["rich_text"]).ReplaceLineEndings(" ").Trim();
        if (text.Length == 0)
        {
            return "";
        }

        return text.Length <= BlockPreviewLength ? $" \"{text}\"" : $" \"{text[..BlockPreviewLength]}...\"";
    }

    /// <summary>The new parent of a moved page: a regular page, or a data source resolved from a database id or URL.</summary>
    private async Task<MoveTarget> BuildMoveTargetAsync(
        string token,
        string pageId,
        string? newParentPage,
        string? newParentDataSource,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(newParentDataSource))
        {
            // The move endpoint takes a data source id, not a database id; resolve it like create_page does.
            var dataSource = await dataSources.ResolveAsync(token, newParentDataSource, cancellationToken);
            var dataSourceId = J.Str(dataSource["id"])!;
            var dataSourceParent = new JsonObject { ["type"] = "data_source_id", ["data_source_id"] = dataSourceId };
            return new MoveTarget(dataSourceParent, $"data source {dataSourceId}");
        }

        var parentPageId = NotionIds.Parse(newParentPage!);
        if (string.Equals(NotionIds.Compact(parentPageId), NotionIds.Compact(pageId), StringComparison.Ordinal))
        {
            throw new ToolException("A page cannot be moved under itself. Pass a different new_parent_page_id.");
        }

        var pageParent = new JsonObject { ["type"] = "page_id", ["page_id"] = parentPageId };
        return new MoveTarget(pageParent, $"page {parentPageId}");
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

    private sealed record MoveTarget(JsonObject Parent, string Destination);
}
