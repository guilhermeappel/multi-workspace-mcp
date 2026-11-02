using System.Text;
using System.Text.Json.Nodes;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Mcp.Tools;

/// <summary>The readable description of a data source that fetch_data_source and update_data_source both show.</summary>
internal static class DataSourceText
{
    public static string Describe(JsonObject dataSource, JsonNode? databaseIcon)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {ToolText.Fallback(RichText.Plain(dataSource["title"]))}");
        sb.AppendLine($"data_source_id: {J.Str(dataSource["id"])}");
        if (J.Str(dataSource["parent"]?["database_id"]) is { } databaseId)
        {
            sb.AppendLine($"database_id: {databaseId}");
        }

        if (Appearance.Describe(databaseIcon) is { } icon)
        {
            sb.AppendLine($"icon: {icon}");
        }

        sb.AppendLine().AppendLine("Properties:");
        foreach (var (name, property) in dataSource["properties"] as JsonObject ?? [])
        {
            var type = J.Str(property?["type"]) ?? "?";
            var line = $"- {name} ({type})";
            if (type is PropertyTypes.Select or PropertyTypes.MultiSelect or PropertyTypes.Status)
            {
                var options = J.Arr(property?[type]?["options"]).Select(Option).ToList();
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

    private static string Option(JsonNode? option) =>
        J.Str(option?["color"]) is { Length: > 0 } color ? $"{J.Str(option?["name"])} ({color})" : J.Str(option?["name"]) ?? "";
}
