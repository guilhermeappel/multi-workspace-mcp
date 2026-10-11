namespace NotionMultiMcp.Notion;

/// <summary>An error answered by the Notion API (or a failure to reach it), with a hint for the model.</summary>
internal sealed class NotionApiException(int status, string code, string message, bool isRead = true) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    /// <summary>False when the failed call could have changed data, so the outcome is unknown.</summary>
    public bool IsRead { get; } = isRead;

    public string ToToolMessage()
    {
        var hint = Code switch
        {
            "object_not_found" =>
                "\nHint: the page/database was not found or is not shared with this workspace's integration " +
                "(in Notion: ••• → Connections → add the integration). Ids from one workspace don't work in another.",
            "unauthorized" => "\nHint: the integration token for this workspace is invalid or was revoked.",
            "restricted_resource" =>
                "\nHint: the integration lacks a capability (e.g. insert/update content). Check its settings in Notion.",
            "rate_limited" => "\nHint: Notion rate limit hit; wait a few seconds and retry.",
            "timeout" or "unreachable" when !IsRead =>
                "\nHint: the write may or may not have been applied. Check the page in Notion before retrying, " +
                "to avoid creating it twice.",
            "timeout" or "unreachable" => "\nHint: a temporary problem reaching Notion; try again in a moment.",
            _ => "",
        };

        return $"Notion API error {Status} ({Code}): {Message}{hint}";
    }
}
