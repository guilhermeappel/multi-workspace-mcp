using System.Text.Json.Nodes;

namespace NotionMultiMcp.Mcp.Tools;

/// <summary>Reading and checking the arguments of a tool call.</summary>
internal static class ToolArguments
{
    public const int DefaultSearchPageSize = 20;
    public const int DefaultQueryPageSize = 25;

    private const int MinPageSize = 1;
    private const int MaxPageSize = 100;

    public static string Required(JsonObject args, string name) =>
        J.Str(args[name]) is { Length: > 0 } value ? value : throw new ToolException($"Missing required argument '{name}'.");

    /// <summary>The page size asked for, kept inside what Notion accepts.</summary>
    public static int Limit(JsonObject args, int fallback) =>
        Math.Clamp(J.Int(args["limit"]) ?? fallback, MinPageSize, MaxPageSize);
}
