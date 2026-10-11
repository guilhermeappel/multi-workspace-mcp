using System.Collections.Frozen;
using System.Text.RegularExpressions;
using NotionMultiMcp.Configuration;

namespace NotionMultiMcp.Notion;

/// <summary>
/// The configured workspaces: each <c>Notion:Workspaces:&lt;name&gt;</c> entry becomes a workspace called
/// &lt;name&gt; (lowercase, underscores turned into hyphens). E.g. <c>Notion__Workspaces__work</c> and
/// <c>Notion__Workspaces__personal</c> become "work" and "personal".
/// </summary>
internal sealed partial class WorkspaceRegistry
{
    private readonly FrozenDictionary<string, string> _tokens;
    private readonly FrozenSet<string> _readOnly;

    private WorkspaceRegistry(Dictionary<string, string> tokens, IEnumerable<string> readOnly)
    {
        _tokens = tokens.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _readOnly = readOnly.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        Names = tokens.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> Names { get; }

    /// <summary>The name Claude sees for a configuration key: lowercase, with underscores as hyphens.</summary>
    public static string NormalizeName(string key) => key.Trim().ToLowerInvariant().Replace('_', '-');

    public static bool IsValidName(string normalizedName) => ValidName().IsMatch(normalizedName);

    /// <summary>The normalized names in a comma separated list such as <c>Notion:ReadOnlyWorkspaces</c>.</summary>
    public static IReadOnlyList<string> ParseNames(string? list) =>
        (list ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Builds the registry from options that already passed <see cref="NotionOptionsValidator"/>.</summary>
    public static WorkspaceRegistry From(NotionOptions options)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, token) in options.Workspaces)
        {
            tokens[NormalizeName(key)] = token!.Trim();
        }

        return new WorkspaceRegistry(tokens, ParseNames(options.ReadOnlyWorkspaces));
    }

    /// <summary>
    /// Whether the tools that change something are refused for the workspace a call names. An omitted name means the
    /// only configured workspace, as in <see cref="TokenFor"/>; an unknown name is not read-only here and is rejected
    /// later with the list of valid names.
    /// </summary>
    public bool IsReadOnly(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return _tokens.Count == 1 && _readOnly.Contains(Names[0]);
        }

        return _readOnly.Contains(NormalizeName(name));
    }

    public string TokenFor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            if (_tokens.Count == 1)
            {
                return _tokens.Values.First();
            }

            throw new ToolException($"Argument 'workspace' is required. Options: {string.Join(", ", Names)}.");
        }

        return _tokens.TryGetValue(name.Trim(), out var token)
            ? token
            : throw new ToolException($"Unknown workspace '{name}'. Options: {string.Join(", ", Names)}.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex ValidName();
}
