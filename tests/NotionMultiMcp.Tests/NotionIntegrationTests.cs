using System.Text.Json.Nodes;
using NotionMultiMcp.Configuration;
using NotionMultiMcp.Mcp.Tools;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Tests;

/// <summary>
/// Runs the icon, schema and database tools against a real Notion workspace. Off by default: the tests are skipped
/// unless <c>NOTION_INTEGRATION_TESTS=1</c> is set, together with <c>NOTION_INTEGRATION_TOKEN</c> (the token of the
/// integration of the personal workspace) and <c>NOTION_INTEGRATION_PARENT_PAGE_ID</c> (a page shared with that
/// integration, under which the test creates its own page). The test page is moved to the trash at the end.
/// </summary>
public sealed class NotionIntegrationTests
{
    private const string Workspace = "personal";

    [IntegrationFact]
    public async Task Icons_schema_changes_and_database_creation_work_against_notion()
    {
        var token = Environment.GetEnvironmentVariable("NOTION_INTEGRATION_TOKEN")!;
        var parentId = Environment.GetEnvironmentVariable("NOTION_INTEGRATION_PARENT_PAGE_ID")!;

        using var http = new HttpClient { BaseAddress = NotionClient.BaseAddress, Timeout = NotionClient.RequestTimeout };
        var notion = new NotionClient(http, TimeProvider.System);
        var options = new NotionOptions { Workspaces = { [Workspace] = token } };
        var registry = WorkspaceRegistry.From(options);
        var context = new ToolContext(notion, registry);
        var resolver = new DataSourceResolver(notion);
        var write = new WriteTools(context, resolver);
        var schema = new SchemaTools(context, resolver);
        var read = new ReadTools(context, resolver);

        var testPageId = default(string);
        try
        {
            var created = await write.CreatePageAsync(
                Args(new JsonObject { ["parent_page_id"] = parentId, ["title"] = "mcp integration test (safe to delete)", ["icon"] = "🧪" }),
                CancellationToken.None);
            testPageId = Field(created, "id");

            var page = await notion.GetAsync(token, $"pages/{testPageId}", CancellationToken.None);
            Assert.Equal("🧪", (string)page["icon"]!["emoji"]!);

            await write.UpdatePageAsync(Args(new JsonObject { ["page_id"] = testPageId, ["icon"] = "https://www.notion.so/images/page-cover/gradients_1.png" }), CancellationToken.None);
            page = await notion.GetAsync(token, $"pages/{testPageId}", CancellationToken.None);
            Assert.Equal("external", (string)page["icon"]!["type"]!);

            await write.UpdatePageAsync(Args(new JsonObject { ["page_id"] = testPageId, ["icon"] = "none" }), CancellationToken.None);
            page = await notion.GetAsync(token, $"pages/{testPageId}", CancellationToken.None);
            Assert.Null(page["icon"]);

            var database = await schema.CreateDatabaseAsync(
                Args(new JsonObject
                {
                    ["parent_page_id"] = testPageId,
                    ["title"] = "Career test",
                    ["icon"] = "🧭",
                    ["properties"] = new JsonObject
                    {
                        ["Name"] = "title",
                        ["Tickets"] = "url",
                        ["Status"] = new JsonObject { ["select"] = new JsonArray("Todo", "Done") },
                    },
                }),
                CancellationToken.None);
            var dataSourceId = Field(database, "data_source_id");

            var updated = await schema.UpdateDataSourceAsync(
                Args(new JsonObject
                {
                    ["data_source_id"] = dataSourceId,
                    ["title"] = "Deliveries test",
                    ["icon"] = "🚚",
                    ["rename_properties"] = new JsonObject { ["Tickets"] = "Tickets count" },
                    ["change_property_types"] = new JsonObject { ["Tickets"] = "number" },
                    ["set_select_options"] = new JsonObject { ["Status"] = new JsonArray("Done", "Highlight") },
                }),
                CancellationToken.None);
            Assert.Contains("AFTER", updated, StringComparison.Ordinal);

            var fetched = await read.FetchDataSourceAsync(Args(new JsonObject { ["data_source_id"] = dataSourceId }), CancellationToken.None);
            Assert.Contains("# Deliveries test", fetched, StringComparison.Ordinal);
            Assert.Contains("icon: 🚚", fetched, StringComparison.Ordinal);
            Assert.Contains("- Tickets count (number)", fetched, StringComparison.Ordinal);
            Assert.Contains("Done (", fetched, StringComparison.Ordinal);
            Assert.Contains("Highlight", fetched, StringComparison.Ordinal);
            Assert.DoesNotContain("Todo", fetched, StringComparison.Ordinal);
        }
        finally
        {
            if (testPageId is not null)
            {
                await notion.PatchAsync(token, $"pages/{testPageId}", new JsonObject { ["in_trash"] = true }, CancellationToken.None);
            }
        }
    }

    private static JsonObject Args(JsonObject arguments)
    {
        arguments["workspace"] = Workspace;
        return arguments;
    }

    private static string Field(string text, string name) =>
        text.ReplaceLineEndings("\n").Split('\n').First(l => l.StartsWith(name + ": ", StringComparison.Ordinal))[(name.Length + 2)..].Trim();
}

/// <summary>A fact that is skipped unless the integration test settings are in the environment.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        var missing = new[] { "NOTION_INTEGRATION_TESTS", "NOTION_INTEGRATION_TOKEN", "NOTION_INTEGRATION_PARENT_PAGE_ID" }
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToList();
        if (missing.Count > 0 || Environment.GetEnvironmentVariable("NOTION_INTEGRATION_TESTS") != "1")
        {
            Skip = "Integration test off: set NOTION_INTEGRATION_TESTS=1, NOTION_INTEGRATION_TOKEN and NOTION_INTEGRATION_PARENT_PAGE_ID.";
        }
    }
}
