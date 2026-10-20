using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotionMultiMcp.Notion;

/// <summary>
/// Thin HTTP client for the Notion REST API. The token decides which workspace is accessed.
/// Retry policy: a 429 is always retried (Notion did not process the request). Server errors and
/// network failures are retried only for reads, because repeating a write that Notion may have
/// applied would duplicate content.
/// </summary>
internal sealed class NotionClient(HttpClient http, TimeProvider timeProvider)
{
    public const string ApiVersion = "2026-03-11";

    internal const int MaxAttempts = 3;

    internal static readonly Uri BaseAddress = new("https://api.notion.com/v1/");
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private const int TooManyRequestsStatus = 429;
    private const int ServerErrorStatus = 500;
    private const int BadGatewayStatus = 502;
    private const int ServiceUnavailableStatus = 503;
    private const int GatewayTimeoutStatus = 504;

    private static readonly TimeSpan _maxRetryDelay = TimeSpan.FromSeconds(10);

    public Task<JsonObject> GetAsync(string token, string path, CancellationToken cancellationToken) =>
        SendAsync(token, HttpMethod.Get, path, null, isRead: true, cancellationToken);

    /// <summary>A POST that changes data (for example creating a page). Never retried after a server error.</summary>
    public Task<JsonObject> PostAsync(string token, string path, JsonNode body, CancellationToken cancellationToken) =>
        SendAsync(token, HttpMethod.Post, path, body, isRead: false, cancellationToken);

    /// <summary>A POST that only reads (search, database query). Safe to retry like a GET.</summary>
    public Task<JsonObject> PostReadOnlyAsync(string token, string path, JsonNode body, CancellationToken cancellationToken) =>
        SendAsync(token, HttpMethod.Post, path, body, isRead: true, cancellationToken);

    public Task<JsonObject> PatchAsync(string token, string path, JsonNode body, CancellationToken cancellationToken) =>
        SendAsync(token, HttpMethod.Patch, path, body, isRead: false, cancellationToken);

    /// <summary>A DELETE (Notion moves the object to the trash). A write, so never retried after a server error.</summary>
    public Task<JsonObject> DeleteAsync(string token, string path, CancellationToken cancellationToken) =>
        SendAsync(token, HttpMethod.Delete, path, null, isRead: false, cancellationToken);

    private async Task<JsonObject> SendAsync(
        string token,
        HttpMethod method,
        string path,
        JsonNode? body,
        bool isRead,
        CancellationToken cancellationToken)
    {
        var payload = body?.ToJsonString();

        for (var attempt = 1; ; attempt++)
        {
            var canRetry = attempt < MaxAttempts;
            var outcome = await TrySendOnceAsync(token, method, path, payload, isRead, cancellationToken);

            if (outcome.Reply is not { } reply)
            {
                // Network failure or timeout: only a read may be repeated, a write may already have been applied.
                if (isRead && canRetry)
                {
                    await Task.Delay(BackoffDelay(attempt), timeProvider, cancellationToken);
                    continue;
                }

                throw outcome.Failure!;
            }

            if (reply.IsSuccess)
            {
                return JsonNode.Parse(reply.Body) as JsonObject
                    ?? throw new NotionApiException(BadGatewayStatus, "invalid_json", "Notion returned an unexpected response.");
            }

            // Rate limit (about 3 requests/s per integration) and, for reads, transient failures: try again.
            var retryable = reply.Status == TooManyRequestsStatus || (isRead && reply.Status >= ServerErrorStatus);
            if (retryable && canRetry)
            {
                var delay = reply.RetryAfter is { } retryAfter ? Cap(retryAfter) : BackoffDelay(attempt);
                await Task.Delay(delay, timeProvider, cancellationToken);
                continue;
            }

            throw ToException(reply.Status, reply.Body);
        }
    }

    /// <summary>
    /// One HTTP round trip. Transport failures come back as a value, not an exception, so the retry loop
    /// does not need a try/catch around variables declared before it.
    /// </summary>
    private async Task<SendOutcome> TrySendOnceAsync(
        string token,
        HttpMethod method,
        string path,
        string? payload,
        bool isRead,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Notion-Version", ApiVersion);
            if (payload is not null)
            {
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var reply = new Reply((int)response.StatusCode, response.IsSuccessStatusCode, text, response.Headers.RetryAfter?.Delta);
            return new SendOutcome(reply, null);
        }
        catch (HttpRequestException e)
        {
            var failure = new NotionApiException(ServiceUnavailableStatus, "unreachable", $"Could not reach the Notion API: {e.Message}", isRead);
            return new SendOutcome(null, failure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout elapsed; the caller did not cancel.
            var failure = new NotionApiException(GatewayTimeoutStatus, "timeout", "The request to the Notion API timed out.", isRead);
            return new SendOutcome(null, failure);
        }
    }

    private static NotionApiException ToException(int status, string text)
    {
        var code = "http_" + status;
        var message = text;
        try
        {
            var error = JsonNode.Parse(text);
            code = J.Str(error?["code"]) ?? code;
            message = J.Str(error?["message"]) ?? message;
        }
        catch (JsonException)
        {
            // Non-JSON body: keep the raw text.
        }

        return new NotionApiException(status, code, message);
    }

    private sealed record Reply(int Status, bool IsSuccess, string Body, TimeSpan? RetryAfter);

    private sealed record SendOutcome(Reply? Reply, NotionApiException? Failure);

    private static TimeSpan BackoffDelay(int attempt) => TimeSpan.FromSeconds(attempt);

    private static TimeSpan Cap(TimeSpan delay) => delay > _maxRetryDelay ? _maxRetryDelay : delay;
}
