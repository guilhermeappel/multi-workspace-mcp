using System.Diagnostics;
using System.Text.Json.Nodes;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Mcp.Tools;

/// <summary>Runs a tools/call: finds the tool, turns failures into results, bounds the output and logs the call.</summary>
internal sealed partial class ToolDispatcher(ToolCatalog catalog, WorkspaceRegistry workspaces, ILogger<ToolDispatcher> logger)
{
    private const int MaxOutputChars = 60_000;
    private const string UnknownWorkspace = "-";

    public async Task<JsonObject> CallAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var name = J.Str(parameters?["name"]) ?? throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, "Missing tool name.");
        if (!catalog.TryGet(name, out var tool))
        {
            throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, $"Unknown tool: {name}");
        }

        var args = parameters?["arguments"] as JsonObject ?? [];
        var stopwatch = Stopwatch.StartNew();
        var outcome = await InvokeAsync(tool, args, cancellationToken);

        LogCall(name, ConfiguredWorkspace(args), !outcome.IsError, stopwatch.ElapsedMilliseconds);

        var text = outcome.Text.Length > MaxOutputChars
            ? outcome.Text[..MaxOutputChars] + "\n\n[output truncated]"
            : outcome.Text;

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = outcome.IsError,
        };
    }

    /// <summary>Runs a tool and turns every failure the model can act on into a result instead of an exception.</summary>
    private async Task<ToolOutcome> InvokeAsync(Tool tool, JsonObject args, CancellationToken cancellationToken)
    {
        try
        {
            return new ToolOutcome(await tool.Handler(args, cancellationToken), IsError: false);
        }
        catch (ToolException e)
        {
            return new ToolOutcome(e.Message, IsError: true);
        }
        catch (NotionApiException e)
        {
            return new ToolOutcome(e.ToToolMessage(), IsError: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            LogFailure(e, tool.Name);
            return new ToolOutcome("Unexpected server error: " + e.Message, IsError: true);
        }
    }

    /// <summary>The workspace name for the log, only when it is one that is configured (never raw client input).</summary>
    private string ConfiguredWorkspace(JsonObject args)
    {
        var requested = J.Str(args["workspace"])?.Trim();
        return workspaces.Names.FirstOrDefault(n => string.Equals(n, requested, StringComparison.OrdinalIgnoreCase)) ?? UnknownWorkspace;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool {Tool} on workspace {Workspace} finished: ok={Ok} in {ElapsedMs} ms")]
    private partial void LogCall(string tool, string workspace, bool ok, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tool {Tool} failed")]
    private partial void LogFailure(Exception exception, string tool);

    private sealed record ToolOutcome(string Text, bool IsError);
}
