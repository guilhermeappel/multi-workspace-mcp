namespace NotionMultiMcp;

/// <summary>Error shown to the model as the tool result (isError = true).</summary>
internal sealed class ToolException(string message) : Exception(message);
