using System.Text;
using System.Text.Json.Nodes;

namespace NotionMultiMcp.Mcp.Tools;

/// <summary>Small pieces of text that several tools produce the same way.</summary>
internal static class ToolText
{
    public static string Fallback(string title) => title.Length > 0 ? title : "(untitled)";

    public static void AppendCursor(StringBuilder sb, JsonObject response)
    {
        if (J.Bool(response["has_more"]) && J.Str(response["next_cursor"]) is { } next)
        {
            sb.AppendLine().AppendLine($"More results available. next_cursor: {next}");
        }
    }
}
