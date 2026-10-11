using System.Collections.Frozen;

namespace NotionMultiMcp.Auth;

/// <summary>Which redirect URIs a client may register and use.</summary>
internal sealed class RedirectPolicy(ServerSettings settings)
{
    private static readonly FrozenSet<string> _builtIn = new[]
    {
        "https://claude.ai/api/mcp/auth_callback",
        "https://claude.com/api/mcp/auth_callback",
    }.ToFrozenSet(StringComparer.Ordinal);

    public bool IsAllowed(string uri)
    {
        if (_builtIn.Contains(uri) || settings.ExtraRedirectUris.Contains(uri, StringComparer.Ordinal))
        {
            return true;
        }

        // Claude Code and MCP Inspector use a callback on http://localhost:<random port>.
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            && string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && parsed.IsLoopback;
    }

    /// <summary>Compares redirect URIs; for loopback the port is ignored (RFC 8252).</summary>
    public static bool Matches(string registered, string requested)
    {
        if (string.Equals(registered, requested, StringComparison.Ordinal))
        {
            return true;
        }

        return Uri.TryCreate(registered, UriKind.Absolute, out var a)
            && Uri.TryCreate(requested, UriKind.Absolute, out var b)
            && string.Equals(a.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && string.Equals(b.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && a.IsLoopback && b.IsLoopback
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.AbsolutePath, b.AbsolutePath, StringComparison.Ordinal);
    }
}
