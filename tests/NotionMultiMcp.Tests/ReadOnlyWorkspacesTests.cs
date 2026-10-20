using System.Text.Json.Nodes;
using NotionMultiMcp.Tests.Support;

namespace NotionMultiMcp.Tests;

/// <summary>Notion:ReadOnlyWorkspaces end to end: the tools that change something are refused before Notion is called.</summary>
public sealed class ReadOnlyWorkspacesTests : IAsyncLifetime, IDisposable
{
    private const string PageId = "1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f";

    private readonly McpAppFactory _factory = new(new Dictionary<string, string?> { ["Notion:ReadOnlyWorkspaces"] = "work" });
    private readonly McpTestClient _client;
    private string _token = "";

    public ReadOnlyWorkspacesTests()
    {
        _client = new McpTestClient(_factory);
    }

    public async Task InitializeAsync() => _token = (await _client.SignInAsync()).Tokens.AccessToken;

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _factory.Dispose();

    private async Task<JsonNode> CallAsync(string tool, string workspace, JsonObject arguments)
    {
        arguments["workspace"] = workspace;
        return await _client.RpcAsync(_token, McpTestClient.ToolCall(1, tool, arguments)).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static JsonObject CreatePageArguments() => new() { ["parent_page_id"] = PageId, ["title"] = "x" };

    private static JsonObject AppendArguments() => new() { ["page_id"] = PageId, ["content_markdown"] = "hello" };

    private static JsonObject TrashArguments() => new() { ["page_id"] = PageId, ["in_trash"] = true };

    private static JsonObject MoveArguments() => new() { ["page_id"] = PageId, ["new_parent_page_id"] = "cccccccccccccccccccccccccccccccc" };

    [Theory]
    [InlineData("create_page")]
    [InlineData("append_content")]
    [InlineData("update_page")]
    [InlineData("move_page")]
    public async Task A_tool_that_changes_something_is_refused_in_a_read_only_workspace_without_calling_notion(string tool)
    {
        var arguments = tool switch
        {
            "create_page" => CreatePageArguments(),
            "append_content" => AppendArguments(),
            "move_page" => MoveArguments(),
            _ => TrashArguments(),
        };

        var response = await CallAsync(tool, "work", arguments);

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("read-only", McpTestClient.ToolText(response));
        Assert.Empty(_factory.Notion.Requests);
    }

    [Fact]
    public async Task The_name_is_matched_the_way_workspace_names_are_everywhere_else()
    {
        var response = await CallAsync("append_content", " WORK ", AppendArguments());

        Assert.True(McpTestClient.ToolIsError(response));
        Assert.Contains("read-only", McpTestClient.ToolText(response));
        Assert.Empty(_factory.Notion.Requests);
    }

    [Fact]
    public async Task Reading_and_searching_still_work_in_a_read_only_workspace()
    {
        _factory.Notion.Respond(_ => StubHttpHandler.Ok("""{"results":[]}"""));

        var response = await CallAsync("search", "work", new JsonObject { ["query"] = "x" });

        Assert.False(McpTestClient.ToolIsError(response));
        Assert.NotEmpty(_factory.Notion.Requests);
    }

    [Fact]
    public async Task Another_workspace_is_not_affected()
    {
        _factory.Notion.Respond(_ => StubHttpHandler.Ok("""{"object":"list","results":[]}"""));

        var response = await CallAsync("append_content", "personal", AppendArguments());

        Assert.DoesNotContain("read-only", McpTestClient.ToolText(response));
        Assert.NotEmpty(_factory.Notion.Requests);
    }

    [Fact]
    public async Task List_workspaces_tells_the_model_which_workspace_is_read_only()
    {
        _factory.Notion.Respond(_ => StubHttpHandler.Ok("""{"name":"Bot","bot":{"workspace_name":"Acme"}}"""));

        var response = await _client.RpcAsync(_token, McpTestClient.ToolCall(1, "list_workspaces", []));
        var text = McpTestClient.ToolText(response);

        Assert.Contains("- work [read-only on this server]:", text);
        Assert.Contains("- personal:", text);
    }
}
