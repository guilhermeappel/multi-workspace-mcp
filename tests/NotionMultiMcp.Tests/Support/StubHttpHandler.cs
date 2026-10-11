namespace NotionMultiMcp.Tests.Support;

internal sealed record RecordedRequest(HttpMethod Method, string Path, string? Authorization, string? NotionVersion, string? Body);

/// <summary>Answers with the given responders in order; the last one repeats when calls outnumber them.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private Func<HttpRequestMessage, HttpResponseMessage>[] _responders;

    public StubHttpHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
    {
        _responders = responders;
    }

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>The Notion API path of a request without the version prefix, query string included.</summary>
    public static string PathOf(HttpRequestMessage request) =>
        request.RequestUri!.PathAndQuery.TrimStart('/').Replace("v1/", "", StringComparison.Ordinal);

    public static HttpResponseMessage Json(System.Net.HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json) };

    public static HttpResponseMessage Ok(string json) => Json(System.Net.HttpStatusCode.OK, json);

    public static HttpResponseMessage NotFound() =>
        Json(System.Net.HttpStatusCode.NotFound, """{"object":"error","code":"object_not_found","message":"Not found."}""");

    /// <summary>Replaces the responders and forgets the requests recorded so far.</summary>
    public void Respond(params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
    {
        _responders = responders;
        Requests.Clear();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            PathOf(request),
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Notion-Version", out var versions) ? versions.Single() : null,
            body));

        return _responders[Math.Min(Requests.Count, _responders.Length) - 1](request);
    }
}
