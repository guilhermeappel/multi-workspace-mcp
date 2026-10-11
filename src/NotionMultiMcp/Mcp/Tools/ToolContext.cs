using System.Text.Json.Nodes;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Mcp.Tools;

/// <summary>What every tool handler needs: the Notion client and the tokens of the configured workspaces.</summary>
internal sealed class ToolContext(NotionClient notion, WorkspaceRegistry workspaces)
{
    public NotionClient Notion { get; } = notion;

    public WorkspaceRegistry Workspaces { get; } = workspaces;

    /// <summary>The token of the workspace named by the call's workspace argument.</summary>
    public string Token(JsonObject args) => Workspaces.TokenFor(J.Str(args["workspace"]));
}
