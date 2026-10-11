using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NotionMultiMcp.Tests.Support;

namespace NotionMultiMcp.Tests;

public sealed class GlobalExceptionHandlerTests
{
    private readonly LogCollector _logs = new();

    private async Task<(DefaultHttpContext Context, JsonNode Body)> HandleAsync(string path, Exception exception)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        var handler = new GlobalExceptionHandler(loggerFactory.CreateLogger<GlobalExceptionHandler>());
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        context.Response.Body.Position = 0;
        return (context, JsonNode.Parse(context.Response.Body)!);
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_a_json_rpc_internal_error_on_mcp_without_leaking_details()
    {
        var (context, body) = await HandleAsync("/mcp", new InvalidOperationException("secret connection string"));

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(-32603, (int)body["error"]!["code"]!);
        Assert.DoesNotContain("secret", body.ToJsonString());
    }

    [Fact]
    public async Task An_unexpected_exception_elsewhere_becomes_a_server_error_response()
    {
        var (context, body) = await HandleAsync("/token", new InvalidOperationException("secret"));

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("server_error", (string)body["error"]!);
        Assert.StartsWith("application/json", context.Response.ContentType);
    }

    [Fact]
    public async Task A_server_error_is_logged_with_its_exception()
    {
        await HandleAsync("/token", new InvalidOperationException("boom"));

        var entry = Assert.Single(_logs.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("/token", entry.Message);
    }

    [Theory]
    [InlineData(413)]
    [InlineData(400)]
    public async Task A_request_the_server_refused_keeps_its_status_and_is_not_a_server_error(int status)
    {
        var (context, body) = await HandleAsync("/authorize", new BadHttpRequestException("too big", status));

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("invalid_request", (string)body["error"]!);
        Assert.Equal(LogLevel.Warning, Assert.Single(_logs.Entries).Level);
    }

    [Fact]
    public async Task Invalid_data_is_a_bad_request()
    {
        var (context, _) = await HandleAsync("/authorize", new InvalidDataException("bad multipart"));

        Assert.Equal(400, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_bad_request_to_mcp_is_a_json_rpc_invalid_request()
    {
        var (context, body) = await HandleAsync("/mcp", new BadHttpRequestException("bad", 400));

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(-32600, (int)body["error"]!["code"]!);
    }
}
