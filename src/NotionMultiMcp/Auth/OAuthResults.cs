using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;

namespace NotionMultiMcp.Auth;

/// <summary>Responses and request limits shared by the OAuth endpoints.</summary>
internal static class OAuthResults
{
    /// <summary>The OAuth endpoints are unauthenticated, so they only accept small bodies.</summary>
    private const int MaxBodyBytes = 16 * 1024;

    public static IResult Error(string error, string description, int status = StatusCodes.Status400BadRequest) =>
        TypedResults.Json(new { error, error_description = description }, statusCode: status);

    public static IResult BodyTooLarge() =>
        Error(OAuthErrorCode.InvalidRequest, "Request body too large.", StatusCodes.Status413PayloadTooLarge);

    /// <summary>303 See Other: after a POST the browser must follow the redirect with a GET.</summary>
    public static IResult SeeOther(string location) => new SeeOtherResult(location);

    /// <summary>Marks a response that carries credentials as not cacheable, for HTTP/1.0 caches too.</summary>
    public static void NoStore(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Pragma = "no-cache";
    }

    /// <summary>Applies the small body cap; false when the declared length already exceeds it.</summary>
    public static bool AcceptsBodySize(HttpContext ctx)
    {
        var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = MaxBodyBytes;
        }

        return ctx.Request.ContentLength is null or <= MaxBodyBytes;
    }

    public static string WithQuery(string uri, params (string Key, string? Value)[] values) =>
        QueryHelpers.AddQueryString(uri, values
            .Where(v => v.Value is not null)
            .Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)));

    private sealed class SeeOtherResult(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = location;
            return Task.CompletedTask;
        }
    }
}
