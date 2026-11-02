using System.Text;
using System.Text.Json.Nodes;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Mcp.Tools;

/// <summary>The tools that change the structure of a database: its name, icon and columns, and creating one.</summary>
internal sealed class SchemaTools(ToolContext context, DataSourceResolver dataSources)
{
    public async Task<string> UpdateDataSourceAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var token = context.Token(args);
        var before = await dataSources.ResolveAsync(token, ToolArguments.Required(args, "data_source_id"), cancellationToken);
        var dataSourceId = J.Str(before["id"])!;
        var databaseId = J.Str(before["parent"]?["database_id"]);

        var title = J.Str(args["title"]) is { } t && t.Trim().Length > 0 ? t.Trim() : null;
        var icon = Appearance.Icon(J.Str(args["icon"]));
        var change = SchemaPatch.Build(
            before["properties"] as JsonObject ?? [],
            StringMap(args, "rename_properties"),
            StringMap(args, "change_property_types"),
            J.ObjectArg(args["set_select_options"]),
            StringMap(args, "add_properties"),
            J.ArrayArg(args["remove_properties"]));

        if (title is null && icon is null && change.Properties.Count == 0)
        {
            var why = change.Notes.Count > 0 ? "\n" + string.Join("\n", change.Notes) : "";
            throw new ToolException(
                "Nothing to update: pass title, icon, rename_properties, change_property_types, set_select_options, add_properties and/or remove_properties." + why);
        }

        var beforeIcon = await dataSources.DatabaseIconAsync(token, before, cancellationToken);

        // The schema (and the name and icon the data source itself carries) goes to the data source...
        var dataSourceBody = new JsonObject();
        if (title is not null)
        {
            dataSourceBody["title"] = RichText.FromPlain(title);
        }

        Appearance.Apply(dataSourceBody, icon, null);
        if (change.Properties.Count > 0)
        {
            dataSourceBody["properties"] = change.Properties;
        }

        await context.Notion.PatchAsync(token, $"data_sources/{dataSourceId}", dataSourceBody, cancellationToken);

        // ...and the title and icon of the database that holds it go to the database.
        if (databaseId is not null && (title is not null || icon is not null))
        {
            var databaseBody = new JsonObject();
            if (title is not null)
            {
                databaseBody["title"] = RichText.FromPlain(title);
            }

            Appearance.Apply(databaseBody, icon, null);
            try
            {
                await context.Notion.PatchAsync(token, $"databases/{databaseId}", databaseBody, cancellationToken);
            }
            catch (NotionApiException e)
            {
                throw new ToolException(
                    $"The data source {dataSourceId} was updated, but the database {databaseId} was not: {e.ToToolMessage()}\n" +
                    "Do not repeat the whole call: only the title and icon of the database are missing.");
            }
        }

        var after = await context.Notion.GetAsync(token, $"data_sources/{dataSourceId}", cancellationToken);
        var afterIcon = await dataSources.DatabaseIconAsync(token, after, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"Updated data source \"{ToolText.Fallback(RichText.Plain(after["title"]))}\"");
        sb.AppendLine($"data_source_id: {dataSourceId}");
        foreach (var note in change.Notes)
        {
            sb.AppendLine(note);
        }

        sb.AppendLine().AppendLine("BEFORE").AppendLine(DataSourceText.Describe(before, beforeIcon));
        sb.AppendLine("AFTER").Append(DataSourceText.Describe(after, afterIcon));
        return sb.ToString();
    }

    public async Task<string> CreateDatabaseAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var token = context.Token(args);
        var parentPageId = NotionIds.Parse(ToolArguments.Required(args, "parent_page_id"));
        var title = ToolArguments.Required(args, "title");
        var icon = Appearance.Icon(J.Str(args["icon"]));
        var properties = SchemaPatch.BuildNew(J.ObjectArg(args["properties"]));

        var body = new JsonObject
        {
            ["parent"] = new JsonObject { ["type"] = "page_id", ["page_id"] = parentPageId },
            ["title"] = RichText.FromPlain(title),
            ["initial_data_source"] = new JsonObject { ["properties"] = properties },
        };
        if (icon?.Value is not null)
        {
            Appearance.Apply(body, icon, null);
        }

        var database = await context.Notion.PostAsync(token, "databases", body, cancellationToken);
        var databaseId = J.Str(database["id"]);
        var dataSourceId = J.Str(J.Arr(database["data_sources"]).FirstOrDefault()?["id"]);

        var sb = new StringBuilder();
        sb.AppendLine($"Created database \"{title}\"");
        sb.AppendLine($"database_id: {databaseId}");
        sb.AppendLine(dataSourceId is null ? "data_source_id: (not returned; use fetch_data_source with the database id)" : $"data_source_id: {dataSourceId}");
        if (J.Str(database["url"]) is { } url)
        {
            sb.AppendLine($"url: {url}");
        }

        return sb.ToString();
    }

    /// <summary>An argument like {"Old":"New"}, where every value must be text.</summary>
    private static JsonObject? StringMap(JsonObject args, string name) => J.ObjectArg(args[name]);
}
