using System.Net;
using System.Text.Json.Nodes;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Mcp.Tools;

/// <summary>Finds the data source a tool argument refers to.</summary>
internal sealed class DataSourceResolver(NotionClient notion)
{
    /// <summary>Accepts a data source id, a database id (with a single data source) or a URL.</summary>
    public async Task<JsonObject> ResolveAsync(string token, string input, CancellationToken cancellationToken)
    {
        var id = NotionIds.Parse(input);
        var asDataSource = await TryGetAsync(token, $"data_sources/{id}", notFoundOnly: true, cancellationToken);
        if (asDataSource.Value is not null)
        {
            return asDataSource.Value;
        }

        // Not a data source id: it may be a database id, which lists its data sources.
        var asDatabase = await TryGetAsync(token, $"databases/{id}", notFoundOnly: false, cancellationToken);
        if (asDatabase.Value is null)
        {
            throw asDataSource.Error!;
        }

        var sources = J.Arr(asDatabase.Value["data_sources"]);
        return sources.Count switch
        {
            1 => await notion.GetAsync(token, $"data_sources/{J.Str(sources[0]?["id"])}", cancellationToken),
            0 => throw new ToolException("This database has no data sources visible to the integration."),
            _ => throw new ToolException(
                "This database has several data sources; pass one of these ids: " +
                string.Join(", ", sources.Select(s => $"{J.Str(s?["name"])} = {J.Str(s?["id"])}"))),
        };
    }

    /// <summary>A GET whose failure is returned instead of thrown, so the caller can try another lookup.</summary>
    private async Task<LookupResult> TryGetAsync(string token, string path, bool notFoundOnly, CancellationToken cancellationToken)
    {
        try
        {
            return new LookupResult(await notion.GetAsync(token, path, cancellationToken), null);
        }
        catch (NotionApiException e) when (!notFoundOnly || e.Status is (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.NotFound)
        {
            return new LookupResult(null, e);
        }
    }

    private sealed record LookupResult(JsonObject? Value, NotionApiException? Error);
}
