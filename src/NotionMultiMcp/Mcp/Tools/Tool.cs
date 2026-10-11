using System.Text.Json.Nodes;

namespace NotionMultiMcp.Mcp.Tools;

/// <summary>One tool exposed over MCP: its definition and the handler that runs it.</summary>
internal sealed record Tool(
    string Name,
    string Title,
    string Description,
    string SchemaJson,
    bool ReadOnly,
    bool Destructive,
    bool Idempotent,
    bool NeedsWorkspace,
    Func<JsonObject, CancellationToken, Task<string>> Handler);
