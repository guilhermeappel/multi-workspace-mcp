using System.Text.Json.Nodes;
using NotionMultiMcp.Auth;

namespace NotionMultiMcp.Mcp;

/// <summary>
/// MCP "Streamable HTTP" transport in stateless mode: each POST carries one JSON-RPC message
/// and gets the answer as application/json. No SSE and no session.
/// </summary>
internal sealed partial class McpEndpoint(
    ServerSettings settings,
    TokenService tokens,
    McpHandler handler,
    OriginPolicy originPolicy,
    ILogger<McpEndpoint> logger)
{
    /// <summary>Largest request body accepted anywhere (applied to Kestrel): room for big Markdown pages.</summary>
    internal const int MaxBodyBytes = 2 * 1024 * 1024;

    private const string McpPath = "/mcp";
    private const int MaxLoggedOriginLength = 100;

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(McpPath, (Delegate)HandleAsync);

        // This server opens no SSE stream and keeps no session; the spec says to answer 405.
        app.MapGet(McpPath, MethodNotAllowed);
        app.MapDelete(McpPath, MethodNotAllowed);
    }

    private async Task<IResult> HandleAsync(HttpContext ctx)
    {
        // Origin first: a cross-origin request from a browser must not even learn whether a token is valid.
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!originPolicy.IsAllowed(origin))
        {
            LogRejectedOrigin(Truncate(origin));
            return JsonRpcResult(JsonRpc.Error(null, JsonRpcErrorCode.InvalidRequest, "Origin not allowed."), StatusCodes.Status403Forbidden);
        }

        var authorization = ctx.Request.Headers.Authorization.ToString();
        if (!settings.AuthDisabled && !tokens.IsValidAccessToken(authorization))
        {
            LogRejectedToken();
            return Unauthorized(ctx, hadCredentials: authorization.Length > 0);
        }

        if (ctx.Request.Headers.TryGetValue(McpProtocol.VersionHeader, out var version)
            && !McpProtocol.SupportedVersions.Contains(version.ToString()))
        {
            return JsonRpcResult(
                JsonRpc.Error(null, JsonRpcErrorCode.InvalidRequest, $"Unsupported {McpProtocol.VersionHeader}."),
                StatusCodes.Status400BadRequest);
        }

        var parsed = await J.TryParseAsync(ctx.Request.Body, ctx.RequestAborted);
        if (!parsed.IsValid)
        {
            return JsonRpcResult(JsonRpc.Error(null, JsonRpcErrorCode.ParseError, "Parse error"), StatusCodes.Status400BadRequest);
        }

        switch (parsed.Node)
        {
            case JsonArray { Count: > 0 } batch:
                return await HandleBatchAsync(batch, ctx.RequestAborted);
            case JsonObject single:
                var answer = await handler.HandleAsync(single, ctx.RequestAborted);
                return answer is null
                    ? TypedResults.Accepted((string?)null)
                    : JsonRpcResult(answer, IsInvalidRequest(answer) ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK);
            default:
                // An empty batch, or JSON that is neither an object nor an array.
                return JsonRpcResult(JsonRpc.Error(null, JsonRpcErrorCode.InvalidRequest, "Invalid Request"), StatusCodes.Status400BadRequest);
        }
    }

    private async Task<IResult> HandleBatchAsync(JsonArray batch, CancellationToken cancellationToken)
    {
        var responses = new JsonArray();
        foreach (var item in batch)
        {
            if (await handler.HandleAsync(item as JsonObject, cancellationToken) is { } response)
            {
                responses.Add(response);
            }
        }

        return responses.Count == 0 ? TypedResults.Accepted((string?)null) : JsonRpcResult(responses);
    }

    /// <summary>
    /// The 401 that makes Claude start the OAuth login. Without credentials the challenge has no error code;
    /// with a token that failed it says invalid_token, which clients read as "refresh or sign in again"
    /// (RFC 6750 section 3.1). The metadata URL is the one for the /mcp resource (RFC 9728).
    /// </summary>
    private IResult Unauthorized(HttpContext ctx, bool hadCredentials)
    {
        var metadata = $"resource_metadata=\"{settings.PublicBaseUrl}{OAuthPaths.ProtectedResourceMetadata}{McpPath}\"";
        ctx.Response.Headers.WWWAuthenticate = hadCredentials
            ? $"{OAuthProtocol.BearerScheme} error=\"{OAuthErrorCode.InvalidToken}\", {metadata}"
            : $"{OAuthProtocol.BearerScheme} {metadata}";

        var error = hadCredentials ? OAuthErrorCode.InvalidToken : OAuthErrorCode.Unauthorized;
        return TypedResults.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);
    }

    private static IResult MethodNotAllowed(HttpContext ctx)
    {
        ctx.Response.Headers.Allow = "POST";
        return TypedResults.StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    private static bool IsInvalidRequest(JsonObject answer) =>
        J.Int(answer["error"]?["code"]) == JsonRpcErrorCode.InvalidRequest;

    private static string Truncate(string value) =>
        value.Length <= MaxLoggedOriginLength ? value : value[..MaxLoggedOriginLength] + "…";

    private static IResult JsonRpcResult(JsonNode node, int status = StatusCodes.Status200OK) =>
        TypedResults.Text(node.ToJsonString(), "application/json", System.Text.Encoding.UTF8, status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected /mcp request: origin {Origin} is not allowed.")]
    private partial void LogRejectedOrigin(string origin);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected /mcp request: missing or invalid access token.")]
    private partial void LogRejectedToken();
}

internal static class McpEndpointExtensions
{
    public static void MapMcpEndpoint(this WebApplication app) =>
        app.Services.GetRequiredService<McpEndpoint>().Map(app);
}
