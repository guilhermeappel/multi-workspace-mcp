using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using NotionMultiMcp.Configuration;
using NotionMultiMcp.Tests.Support;

namespace NotionMultiMcp.Tests;

/// <summary>
/// Guards for the rules no analyzer can check: they turn a documented convention into a failing test, so the
/// documentation and the code cannot drift apart unnoticed.
/// </summary>
public sealed partial class RepositoryConventionsTests
{
    private static readonly string _root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NotionMultiMcp.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (NotionMultiMcp.slnx) was not found.");
    }

    private static IEnumerable<string> SourceFiles(string folder, string pattern = "*.cs") =>
        Directory.EnumerateFiles(Path.Combine(_root, folder), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string Readme => File.ReadAllText(Path.Combine(_root, "README.md"));

    [GeneratedRegex(@"(?<!Typed)\bResults\.(Json|Text|Content|StatusCode|Redirect|BadRequest|Accepted|Ok|NotFound|Empty)\b")]
    private static partial Regex UntypedResults();

    private static readonly string[] _secretOptions = [nameof(ServerOptions.AuthPassword), nameof(ServerOptions.SigningKey)];

    private static IEnumerable<string> ServerOptionNames() =>
        typeof(ServerOptions).GetProperties().Select(p => p.Name);

    [Fact]
    public void Endpoints_return_typed_results()
    {
        var offenders = SourceFiles("src").Where(f => UntypedResults().IsMatch(File.ReadAllText(f)));

        Assert.Empty(offenders.Select(f => Path.GetRelativePath(_root, f)));
    }

    [Fact]
    public void Every_setting_the_server_reads_is_in_the_readme_settings_table()
    {
        var readme = Readme;

        Assert.All(ServerOptionNames(), name => Assert.Contains($"| `Server:{name}` |", readme, StringComparison.Ordinal));
        Assert.Contains("| `Notion:Workspaces:<name>` |", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_other_notion_setting_is_in_the_readme_settings_table()
    {
        var others = typeof(NotionOptions).GetProperties().Select(p => p.Name).Except([nameof(NotionOptions.Workspaces)]);

        Assert.All(others, name => Assert.Contains($"| `Notion:{name}` |", Readme, StringComparison.Ordinal));
    }

    [Fact]
    public void Appsettings_declares_every_non_secret_server_option_with_its_default()
    {
        var path = Path.Combine(_root, "src", "NotionMultiMcp", "appsettings.json");
        var section = new ConfigurationBuilder().AddJsonFile(path).Build().GetSection(ServerOptions.SectionName);

        var declared = section.GetChildren().Select(c => c.Key).Order().ToList();
        var expected = ServerOptionNames().Except(_secretOptions).Order().ToList();
        Assert.Equal(expected, declared);

        // What the file documents must be what the code does when nothing overrides it.
        Assert.Equivalent(new ServerOptions(), section.Get<ServerOptions>());
    }

    [Fact]
    public void Appsettings_never_holds_a_secret()
    {
        var json = File.ReadAllText(Path.Combine(_root, "src", "NotionMultiMcp", "appsettings.json"));

        Assert.All(_secretOptions, name => Assert.DoesNotContain(name, json, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"Workspaces\": {}", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_tool_is_in_the_readme_tools_table()
    {
        using var factory = new McpAppFactory();
        var client = new McpTestClient(factory);
        var token = (await client.SignInAsync()).Tokens.AccessToken;

        var response = await client.RpcAsync(token, McpTestClient.Request(1, "tools/list"));
        var names = ((JsonArray)response["result"]!["tools"]!).Select(t => (string)t!["name"]!).ToList();

        var readme = Readme;
        Assert.All(names, name => Assert.Contains($"| `{name}` |", readme, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_tool_that_may_change_something_takes_a_workspace_so_the_read_only_setting_can_refuse_it()
    {
        using var factory = new McpAppFactory();
        var client = new McpTestClient(factory);
        var token = (await client.SignInAsync()).Tokens.AccessToken;

        var response = await client.RpcAsync(token, McpTestClient.Request(1, "tools/list"));
        var writers = ((JsonArray)response["result"]!["tools"]!).Where(t => !(bool)t!["annotations"]!["readOnlyHint"]!).ToList();

        Assert.NotEmpty(writers);
        Assert.All(writers, t => Assert.NotNull(t!["inputSchema"]!["properties"]!["workspace"]));
    }

    [Fact]
    public void Source_files_are_written_in_english()
    {
        // Portuguese accents are a cheap, reliable sign of text that was not translated.
        var accented = SourceFiles("src").Concat(SourceFiles("tests"))
            .Where(f => !f.EndsWith("RepositoryConventionsTests.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Any(c => "ãõçÃÕÇ".Contains(c, StringComparison.Ordinal)));

        Assert.Empty(accented.Select(f => Path.GetRelativePath(_root, f)));
    }
}
