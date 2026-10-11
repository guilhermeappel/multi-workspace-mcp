namespace NotionMultiMcp.Mcp;

/// <summary>A failure that is answered as a JSON-RPC error (not as a tool result).</summary>
internal sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
