using NotionMultiMcp.Notion;

namespace NotionMultiMcp;

/// <summary>What the app writes to the log when it starts.</summary>
internal static partial class StartupLog
{
    public static void Describe(ILogger logger, ServerSettings settings, WorkspaceRegistry workspaces)
    {
        LogWorkspaces(logger, string.Join(", ", workspaces.Names));
        LogMcpUrl(logger, settings.McpUrl);
        LogTokenGeneration(logger, settings.TokenGeneration);
        if (settings.AuthDisabled)
        {
            LogAuthDisabled(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Configured workspaces: {Workspaces}")]
    private static partial void LogWorkspaces(ILogger logger, string workspaces);

    [LoggerMessage(Level = LogLevel.Information, Message = "Public MCP URL: {Url}")]
    private static partial void LogMcpUrl(ILogger logger, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token generation: {Generation} (increment Server:TokenGeneration to sign everybody out)")]
    private static partial void LogTokenGeneration(ILogger logger, int generation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server:AuthDisabled=true: /mcp has NO authentication (only allowed in Development).")]
    private static partial void LogAuthDisabled(ILogger logger);
}
