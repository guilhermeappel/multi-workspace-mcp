using System.Collections.Frozen;
using System.Text.Json.Nodes;
using NotionMultiMcp.Mcp.Tools;

namespace NotionMultiMcp.Mcp;

/// <summary>The MCP protocol revisions this server speaks.</summary>
internal static class McpProtocol
{
    public const string LatestVersion = "2025-11-25";
    public const string VersionHeader = "MCP-Protocol-Version";

    public static readonly FrozenSet<string> SupportedVersions =
        new[] { LatestVersion, "2025-06-18", "2025-03-26", "2024-11-05" }.ToFrozenSet(StringComparer.Ordinal);
}

internal sealed partial class McpHandler(ToolCatalog catalog, ToolDispatcher dispatcher, ILogger<McpHandler> logger)
{
    private const string ServerVersion = "1.0.0";
    private const string JsonRpcVersion = "2.0";
    private const int MaxLoggedMethodLength = 100;

    /// <summary>Handles one JSON-RPC message. Returns null for notifications and for responses from the client.</summary>
    public async Task<JsonObject?> HandleAsync(JsonObject? message, CancellationToken cancellationToken)
    {
        if (message is null)
        {
            return InvalidRequest(null);
        }

        var shape = Classify(message);
        switch (shape.Kind)
        {
            case MessageKind.Invalid:
                return InvalidRequest(shape.Id);
            case MessageKind.Notification:
            case MessageKind.ClientResponse:
                return null;
        }

        var method = shape.Method!;
        var id = shape.Id;
        var parameters = message["params"] as JsonObject;

        try
        {
            var result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => [],
                "tools/list" => catalog.ListTools(),
                "tools/call" => await dispatcher.CallAsync(parameters, cancellationToken),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "resources/templates/list" => new JsonObject { ["resourceTemplates"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ => throw new JsonRpcException(JsonRpcErrorCode.MethodNotFound, $"Method not found: {method}"),
            };

            return JsonRpc.Result(id, result);
        }
        catch (JsonRpcException e)
        {
            return JsonRpc.Error(id, e.Code, e.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            LogHandlerFailure(e, Truncate(method));
            return JsonRpc.Error(id, JsonRpcErrorCode.InternalError, "Internal error");
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxLoggedMethodLength ? value : value[..MaxLoggedMethodLength] + "…";

    [LoggerMessage(Level = LogLevel.Error, Message = "Error while handling {Method}")]
    private partial void LogHandlerFailure(Exception exception, string method);

    private static JsonObject InvalidRequest(JsonNode? id) =>
        JsonRpc.Error(id, JsonRpcErrorCode.InvalidRequest, "Invalid Request");

    /// <summary>Tells requests, notifications, client responses and malformed messages apart (JSON-RPC 2.0).</summary>
    private static MessageShape Classify(JsonObject message)
    {
        // An id can be echoed back only if it is a valid one (a string or a number).
        var validId = IsValidId(message["id"]) ? message["id"]!.DeepClone() : null;

        if (!string.Equals(J.Str(message["jsonrpc"]), JsonRpcVersion, StringComparison.Ordinal))
        {
            return new MessageShape(MessageKind.Invalid, null, validId);
        }

        if (message.ContainsKey("method"))
        {
            var method = J.Str(message["method"]);
            if (string.IsNullOrEmpty(method))
            {
                return new MessageShape(MessageKind.Invalid, null, validId);
            }

            if (!message.ContainsKey("id"))
            {
                return new MessageShape(MessageKind.Notification, method, null);
            }

            return validId is null
                ? new MessageShape(MessageKind.Invalid, null, null)
                : new MessageShape(MessageKind.Request, method, validId);
        }

        var isClientResponse = message.ContainsKey("id") && (message.ContainsKey("result") || message.ContainsKey("error"));
        return new MessageShape(isClientResponse ? MessageKind.ClientResponse : MessageKind.Invalid, null, validId);
    }

    private static bool IsValidId(JsonNode? id) =>
        id is JsonValue value && (value.TryGetValue<string>(out _) || value.TryGetValue<double>(out _));

    private enum MessageKind
    {
        Request,
        Notification,
        ClientResponse,
        Invalid,
    }

    private sealed record MessageShape(MessageKind Kind, string? Method, JsonNode? Id);

    private JsonObject Initialize(JsonObject? parameters)
    {
        var requested = J.Str(parameters?["protocolVersion"]);
        var version = requested is not null && McpProtocol.SupportedVersions.Contains(requested)
            ? requested
            : McpProtocol.LatestVersion;

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "multi-workspace-mcp",
                ["title"] = "Notion (multi-workspace)",
                ["version"] = ServerVersion,
            },
            ["instructions"] = catalog.Instructions,
        };
    }
}
