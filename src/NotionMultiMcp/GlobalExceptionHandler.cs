using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Diagnostics;
using NotionMultiMcp.Auth;
using NotionMultiMcp.Mcp;

namespace NotionMultiMcp;

/// <summary>
/// Last line of defence for anything thrown outside the tool path (a malformed form body, an unexpected
/// bug): log it, and answer in the shape the endpoint's clients understand, without leaking details.
/// </summary>
internal sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private const string McpPath = "/mcp";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var status = StatusOf(exception);
        var path = httpContext.Request.Path.Value ?? string.Empty;
        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(exception, path);
        }
        else
        {
            LogMalformed(path, exception.GetType().Name);
        }

        httpContext.Response.StatusCode = status;
        var body = httpContext.Request.Path.StartsWithSegments(McpPath, StringComparison.Ordinal)
            ? JsonRpc.Error(null, status >= StatusCodes.Status500InternalServerError ? JsonRpcErrorCode.InternalError : JsonRpcErrorCode.InvalidRequest, "Request failed")
            : new JsonObject { ["error"] = status >= StatusCodes.Status500InternalServerError ? OAuthErrorCode.ServerError : OAuthErrorCode.InvalidRequest };

        httpContext.Response.ContentType = "application/json";
        await httpContext.Response.WriteAsync(body.ToJsonString(), cancellationToken);
        return true;
    }

    private static int StatusOf(Exception exception) => exception switch
    {
        BadHttpRequestException bad => bad.StatusCode,
        InvalidDataException => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError,
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on {Path}")]
    private partial void LogUnhandled(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a malformed request to {Path}: {Reason}")]
    private partial void LogMalformed(string path, string reason);
}
