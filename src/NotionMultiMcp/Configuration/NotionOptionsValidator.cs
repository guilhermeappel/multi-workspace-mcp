using Microsoft.Extensions.Options;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Configuration;

internal sealed class NotionOptionsValidator : IValidateOptions<NotionOptions>
{
    public ValidateOptionsResult Validate(string? name, NotionOptions options)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, token) in options.Workspaces)
        {
            var workspace = WorkspaceRegistry.NormalizeName(key);
            if (!WorkspaceRegistry.IsValidName(workspace))
            {
                failures.Add($"Notion:Workspaces:{key} is not a valid workspace name (use letters, digits and hyphens).");
            }
            else if (!seen.Add(workspace))
            {
                failures.Add($"Notion:Workspaces:{key} duplicates another entry once underscores become hyphens.");
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                failures.Add($"Notion:Workspaces:{key} has no token.");
            }
        }

        if (options.Workspaces.Count == 0)
        {
            failures.Add("No workspace configured. Set at least one Notion:Workspaces:<name> entry holding an integration token (for example Notion__Workspaces__work).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
