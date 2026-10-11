namespace NotionMultiMcp.Configuration;

/// <summary>
/// The "Notion" configuration section. Each entry of <see cref="Workspaces"/> is a workspace: the key is the
/// name Claude uses and the value is that workspace's integration token (a secret, so it only ever comes from
/// user-secrets or environment variables, e.g. <c>Notion__Workspaces__work</c>).
/// </summary>
internal sealed class NotionOptions
{
    public const string SectionName = "Notion";

    public Dictionary<string, string?> Workspaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names of workspaces in which the tools that change something are refused, separated by commas (for example
    /// <c>work,client-x</c>). A second layer on top of the capabilities given to the Notion integration, which stays
    /// the stronger control. Every name must be a configured workspace.
    /// </summary>
    public string? ReadOnlyWorkspaces { get; set; }
}
