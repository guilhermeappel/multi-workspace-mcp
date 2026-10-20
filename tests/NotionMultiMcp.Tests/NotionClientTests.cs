using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using NotionMultiMcp.Notion;
using NotionMultiMcp.Tests.Support;

namespace NotionMultiMcp.Tests;

public class NotionClientTests
{
    private const string Token = "ntn_test_token";

    // The fake clock never advances by itself, so a call that wrongly waits to retry would hang forever.
    // Bounding the calls that must not retry turns such a regression into a failure instead of a hang.
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private NotionClient CreateClient(StubHttpHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = NotionClient.BaseAddress }, _time);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json) };

    private static HttpResponseMessage WithRetryAfter(HttpResponseMessage response, int seconds)
    {
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    /// <summary>Advances the fake clock until the task completes; returns how far it had to go.</summary>
    private async Task<TimeSpan> AdvanceUntilCompletedAsync(Task task, TimeSpan step)
    {
        var advanced = TimeSpan.Zero;
        var realClock = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Assert.True(realClock.Elapsed < _testTimeout, "The request never completed.");
            _time.Advance(step);
            advanced += step;
            await Task.WhenAny(task, Task.Delay(1));
        }

        return advanced;
    }

    [Fact]
    public async Task Sends_authorization_and_version_headers_and_parses_the_body()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.OK, """{"object":"user","id":"abc"}"""));

        var result = await CreateClient(handler).GetAsync(Token, "users/me", CancellationToken.None);

        Assert.Equal("abc", (string?)result["id"]);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("users/me", request.Path);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Equal(NotionClient.ApiVersion, request.NotionVersion);
    }

    [Fact]
    public async Task Post_sends_the_json_body()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.OK, "{}"));

        await CreateClient(handler).PostAsync(Token, "search", new JsonObject { ["query"] = "plan" }, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("""{"query":"plan"}""", request.Body);
    }

    [Fact]
    public async Task Get_retries_a_transient_failure_and_then_succeeds()
    {
        var handler = new StubHttpHandler(
            _ => Json(HttpStatusCode.ServiceUnavailable, "{}"),
            _ => Json(HttpStatusCode.OK, """{"ok":true}"""));
        var call = CreateClient(handler).GetAsync(Token, "pages/1", CancellationToken.None);

        await AdvanceUntilCompletedAsync(call, TimeSpan.FromMilliseconds(100));

        Assert.True((bool?)(await call)["ok"]);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Gives_up_after_the_maximum_number_of_attempts()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.BadGateway, """{"code":"bad_gateway","message":"upstream"}"""));
        var call = CreateClient(handler).GetAsync(Token, "pages/1", CancellationToken.None);

        await AdvanceUntilCompletedAsync(call, TimeSpan.FromMilliseconds(500));

        var error = await Assert.ThrowsAsync<NotionApiException>(() => call);
        Assert.Equal(502, error.Status);
        Assert.Equal(NotionClient.MaxAttempts, handler.Requests.Count);
    }

    [Fact]
    public async Task Rate_limit_waits_for_the_retry_after_header()
    {
        var handler = new StubHttpHandler(
            _ => WithRetryAfter(Json((HttpStatusCode)429, "{}"), seconds: 3),
            _ => Json(HttpStatusCode.OK, "{}"));
        var call = CreateClient(handler).GetAsync(Token, "pages/1", CancellationToken.None);

        var waited = await AdvanceUntilCompletedAsync(call, TimeSpan.FromMilliseconds(100));

        // The default backoff for the first retry is 1 s, so waiting 3 s proves the header was honoured.
        Assert.True(waited >= TimeSpan.FromSeconds(3), $"Only waited {waited}.");
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Retry_after_is_capped_at_ten_seconds()
    {
        var handler = new StubHttpHandler(
            _ => WithRetryAfter(Json((HttpStatusCode)429, "{}"), seconds: 600),
            _ => Json(HttpStatusCode.OK, "{}"));
        var call = CreateClient(handler).GetAsync(Token, "pages/1", CancellationToken.None);

        var waited = await AdvanceUntilCompletedAsync(call, TimeSpan.FromSeconds(1));

        Assert.InRange(waited, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Client_errors_are_not_retried_and_map_the_notion_error_body()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.NotFound, """{"code":"object_not_found","message":"Could not find page"}"""));

        var error = await Assert.ThrowsAsync<NotionApiException>(
            () => CreateClient(handler).GetAsync(Token, "pages/1", CancellationToken.None).WaitAsync(_testTimeout));

        Assert.Equal(404, error.Status);
        Assert.Equal("object_not_found", error.Code);
        Assert.Equal("Could not find page", error.Message);
        Assert.Single(handler.Requests);
        Assert.Contains("Connections", error.ToToolMessage());
    }

    [Fact]
    public async Task Keeps_the_raw_text_when_the_error_body_is_not_json()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("plain failure") });

        var error = await Assert.ThrowsAsync<NotionApiException>(
            () => CreateClient(handler).GetAsync(Token, "pages/1", CancellationToken.None).WaitAsync(_testTimeout));

        Assert.Equal("http_400", error.Code);
        Assert.Equal("plain failure", error.Message);
    }

    [Fact]
    public async Task A_successful_non_object_body_is_reported_as_an_invalid_response()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.OK, "[1,2,3]"));

        var error = await Assert.ThrowsAsync<NotionApiException>(
            () => CreateClient(handler).GetAsync(Token, "pages/1", CancellationToken.None).WaitAsync(_testTimeout));

        Assert.Equal("invalid_json", error.Code);
    }

    [Fact]
    public async Task A_write_is_not_retried_after_a_server_error()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.BadGateway, "{}"), _ => Json(HttpStatusCode.OK, "{}"));

        var error = await Assert.ThrowsAsync<NotionApiException>(
            () => CreateClient(handler).PostAsync(Token, "pages", new JsonObject(), CancellationToken.None).WaitAsync(_testTimeout));

        Assert.Equal(502, error.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_patch_is_not_retried_after_a_server_error()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"), _ => Json(HttpStatusCode.OK, "{}"));

        await Assert.ThrowsAsync<NotionApiException>(
            () => CreateClient(handler).PatchAsync(Token, "blocks/1/children", new JsonObject(), CancellationToken.None).WaitAsync(_testTimeout));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_delete_is_not_retried_after_a_server_error()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.InternalServerError, "{}"), _ => Json(HttpStatusCode.OK, "{}"));

        await Assert.ThrowsAsync<NotionApiException>(
            () => CreateClient(handler).DeleteAsync(Token, "blocks/1", CancellationToken.None).WaitAsync(_testTimeout));

        Assert.Equal(HttpMethod.Delete, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task A_write_is_retried_after_rate_limiting_because_nothing_was_applied()
    {
        var handler = new StubHttpHandler(_ => Json((HttpStatusCode)429, "{}"), _ => Json(HttpStatusCode.OK, """{"id":"p"}"""));
        var call = CreateClient(handler).PostAsync(Token, "pages", new JsonObject(), CancellationToken.None);

        await AdvanceUntilCompletedAsync(call, TimeSpan.FromMilliseconds(100));

        Assert.Equal("p", (string?)(await call)["id"]);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_read_only_post_is_retried_after_a_server_error()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.BadGateway, "{}"), _ => Json(HttpStatusCode.OK, """{"results":[]}"""));
        var call = CreateClient(handler).PostReadOnlyAsync(Token, "search", new JsonObject(), CancellationToken.None);

        await AdvanceUntilCompletedAsync(call, TimeSpan.FromMilliseconds(100));

        Assert.NotNull((await call)["results"]);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_network_failure_on_a_read_is_retried()
    {
        var handler = new StubHttpHandler(
            _ => throw new HttpRequestException("connection reset"),
            _ => Json(HttpStatusCode.OK, """{"ok":true}"""));
        var call = CreateClient(handler).GetAsync(Token, "pages/1", CancellationToken.None);

        await AdvanceUntilCompletedAsync(call, TimeSpan.FromMilliseconds(100));

        Assert.True((bool?)(await call)["ok"]);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_network_failure_on_a_write_is_reported_and_not_retried()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("connection reset"));

        var error = await Assert.ThrowsAsync<NotionApiException>(
            () => CreateClient(handler).PostAsync(Token, "pages", new JsonObject(), CancellationToken.None).WaitAsync(_testTimeout));

        Assert.Equal("unreachable", error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_timeout_on_a_write_warns_that_it_may_have_been_applied()
    {
        var handler = new StubHttpHandler(_ => throw new TaskCanceledException("timed out"));

        var error = await Assert.ThrowsAsync<NotionApiException>(
            () => CreateClient(handler).PostAsync(Token, "pages", new JsonObject(), CancellationToken.None).WaitAsync(_testTimeout));

        Assert.Equal("timeout", error.Code);
        Assert.Equal(504, error.Status);
        Assert.Contains("may or may not have been applied", error.ToToolMessage());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_requested_by_the_caller_is_not_turned_into_an_error()
    {
        var client = new NotionClient(new HttpClient(new HangingHandler()) { BaseAddress = NotionClient.BaseAddress }, _time);
        using var cancellation = new CancellationTokenSource();
        var call = client.GetAsync(Token, "pages/1", cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    /// <summary>Never answers; only the caller's cancellation ends the request, like a stalled connection.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
