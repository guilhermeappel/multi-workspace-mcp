namespace NotionMultiMcp;

/// <summary>Unauthenticated liveness endpoints. They answer HEAD too, because probes and monitors often use it.</summary>
internal static class HealthEndpoints
{
    private static readonly string[] _methods = [HttpMethods.Get, HttpMethods.Head];

    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapMethods("/", _methods, (HttpContext ctx) => NoStoreText(ctx, "multi-workspace-mcp ok. MCP endpoint: /mcp"));
        app.MapMethods("/healthz", _methods, (HttpContext ctx) => NoStoreText(ctx, "ok"));
    }

    private static IResult NoStoreText(HttpContext ctx, string text)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        return TypedResults.Text(text);
    }
}
