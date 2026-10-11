using System.Text.Json.Nodes;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Mcp.Tools;

/// <summary>
/// The tools this server offers: their definitions (with the workspace argument injected into every schema
/// that needs it) and the handler behind each name.
/// </summary>
internal sealed class ToolCatalog
{
    private readonly Dictionary<string, Tool> _tools;
    private readonly JsonObject _toolList;

    public ToolCatalog(ReadTools read, WriteTools write, WorkspaceRegistry workspaces)
    {
        var tools = BuildTools(read, write).ToList();
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _toolList = new JsonObject { ["tools"] = new JsonArray(tools.Select(t => ToolDefinition(t, workspaces.Names)).ToArray()) };

        Instructions =
            $"This server gives access to {workspaces.Names.Count} Notion workspace(s) through one connector: " +
            $"{string.Join(", ", workspaces.Names)}. Every tool takes a `workspace` argument; pick it from the user's " +
            "wording (e.g. work vs personal) and call list_workspaces if unsure. Ids belong to one workspace and " +
            "won't work in another. Page content is exchanged as Markdown. Integrations only see pages that were " +
            "shared with them in Notion.";
    }

    public string Instructions { get; }

    public JsonObject ListTools() => (JsonObject)_toolList.DeepClone();

    public bool TryGet(string name, out Tool tool)
    {
        var found = _tools.TryGetValue(name, out var value);
        tool = value!;
        return found;
    }

    private static IEnumerable<Tool> BuildTools(ReadTools read, WriteTools write)
    {
        yield return new Tool(
            "list_workspaces", "List workspaces",
            "List the Notion workspaces configured on this server and the real Notion workspace each one maps to.",
            """{"type":"object","properties":{}}""",
            ReadOnly: true, Destructive: false, Idempotent: true, NeedsWorkspace: false, read.ListWorkspacesAsync);

        yield return new Tool(
            "search", "Search",
            "Search pages and data sources (databases) by title in one Notion workspace. Omit query to list recently edited items.",
            """
            {"type":"object","properties":{
              "query":{"type":"string","description":"Text to match against titles."},
              "type":{"type":"string","enum":["all","page","data_source"],"default":"all","description":"Restrict to pages or data sources (databases)."},
              "limit":{"type":"integer","minimum":1,"maximum":100,"default":20},
              "cursor":{"type":"string","description":"next_cursor from a previous call."}
            }}
            """,
            ReadOnly: true, Destructive: false, Idempotent: true, NeedsWorkspace: true, read.SearchAsync);

        yield return new Tool(
            "fetch_page", "Fetch page",
            "Read a Notion page: title, properties and (optionally) its content as Markdown, including nested blocks.",
            """
            {"type":"object","required":["page_id"],"properties":{
              "page_id":{"type":"string","description":"Page id or Notion page URL."},
              "include_content":{"type":"boolean","default":true,"description":"Also return the page body as Markdown."}
            }}
            """,
            ReadOnly: true, Destructive: false, Idempotent: true, NeedsWorkspace: true, read.FetchPageAsync);

        yield return new Tool(
            "fetch_data_source", "Fetch data source",
            "Get the schema of a data source (database): property names, types and select/status options. " +
            "Call this before querying or creating rows.",
            """
            {"type":"object","required":["data_source_id"],"properties":{
              "data_source_id":{"type":"string","description":"Data source id, database id, or Notion database URL."}
            }}
            """,
            ReadOnly: true, Destructive: false, Idempotent: true, NeedsWorkspace: true, read.FetchDataSourceAsync);

        yield return new Tool(
            "query_data_source", "Query data source",
            "List rows (pages) of a data source, with optional Notion filter and sorts. " +
            "Filter example: {\"property\":\"Status\",\"status\":{\"equals\":\"Done\"}}; " +
            "compound: {\"and\":[...]}. Sorts example: [{\"property\":\"Due\",\"direction\":\"ascending\"}].",
            """
            {"type":"object","required":["data_source_id"],"properties":{
              "data_source_id":{"type":"string","description":"Data source id, database id, or Notion database URL."},
              "filter":{"type":"object","description":"Notion API filter object."},
              "sorts":{"type":"array","items":{"type":"object"},"description":"Notion API sorts array."},
              "limit":{"type":"integer","minimum":1,"maximum":100,"default":25},
              "cursor":{"type":"string","description":"next_cursor from a previous call."}
            }}
            """,
            ReadOnly: true, Destructive: false, Idempotent: true, NeedsWorkspace: true, read.QueryDataSourceAsync);

        yield return new Tool(
            "create_page", "Create page",
            "Create a page under a parent page, or a new row in a data source. Content is Markdown " +
            "(headings, lists, to-dos, quotes, code, tables, dividers). For data sources, properties accept simple " +
            "values keyed by property name, e.g. {\"Status\":\"Todo\",\"Tags\":[\"a\",\"b\"],\"Due\":\"2026-10-01\"}.",
            """
            {"type":"object","properties":{
              "parent_page_id":{"type":"string","description":"Parent page id or URL. Use this OR parent_data_source_id."},
              "parent_data_source_id":{"type":"string","description":"Data source (database) id or URL to add a row to."},
              "title":{"type":"string"},
              "content_markdown":{"type":"string","description":"Page body in Markdown."},
              "properties":{"type":"object","description":"Only for data source rows. Simple values or raw Notion property objects."}
            }}
            """,
            ReadOnly: false, Destructive: false, Idempotent: false, NeedsWorkspace: true, write.CreatePageAsync);

        yield return new Tool(
            "append_content", "Append content",
            "Append Markdown content to the end of an existing page.",
            """
            {"type":"object","required":["page_id","content_markdown"],"properties":{
              "page_id":{"type":"string","description":"Page id or URL."},
              "content_markdown":{"type":"string"}
            }}
            """,
            ReadOnly: false, Destructive: false, Idempotent: false, NeedsWorkspace: true, write.AppendContentAsync);

        yield return new Tool(
            "update_page", "Update page",
            "Update a page's title and/or properties, or move it to / restore it from the trash.",
            """
            {"type":"object","required":["page_id"],"properties":{
              "page_id":{"type":"string","description":"Page id or URL."},
              "title":{"type":"string"},
              "properties":{"type":"object","description":"Simple values or raw Notion property objects, keyed by property name."},
              "in_trash":{"type":"boolean","description":"true moves the page to the trash; false restores it."}
            }}
            """,
            ReadOnly: false, Destructive: true, Idempotent: true, NeedsWorkspace: true, write.UpdatePageAsync);
    }

    private static JsonObject ToolDefinition(Tool tool, IReadOnlyList<string> workspaceNames)
    {
        var schema = (JsonObject)JsonNode.Parse(tool.SchemaJson)!;
        if (tool.NeedsWorkspace)
        {
            var properties = (JsonObject)schema["properties"]!;
            var withWorkspace = new JsonObject
            {
                ["workspace"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(workspaceNames.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                    ["description"] = "Which Notion workspace to use.",
                },
            };
            foreach (var (key, value) in properties.ToList())
            {
                properties.Remove(key);
                withWorkspace[key] = value;
            }

            schema["properties"] = withWorkspace;

            var required = new JsonArray("workspace");
            foreach (var item in J.Arr(schema["required"]))
            {
                required.Add(J.Str(item));
            }

            schema["required"] = required;
        }

        return new JsonObject
        {
            ["name"] = tool.Name,
            ["title"] = tool.Title,
            ["description"] = tool.Description,
            ["inputSchema"] = schema,
            ["annotations"] = new JsonObject
            {
                ["title"] = tool.Title,
                ["readOnlyHint"] = tool.ReadOnly,
                ["destructiveHint"] = tool.Destructive,
                ["idempotentHint"] = tool.Idempotent,
                ["openWorldHint"] = true,
            },
        };
    }
}
