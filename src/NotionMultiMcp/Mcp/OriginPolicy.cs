using System.Collections.Frozen;

namespace NotionMultiMcp.Mcp;

/// <summary>
/// Decides which browser origins may call /mcp. The MCP transport specification requires servers to validate
/// the Origin header to defend against DNS rebinding. Non-browser clients (Claude's backend, scripts) send no
/// Origin, and that is allowed; the access token is still required.
/// </summary>
internal sealed class OriginPolicy
{
    private static readonly string[] _builtInOrigins = ["https://claude.ai", "https://claude.com"];

    private readonly FrozenSet<string> _allowed;

    public OriginPolicy(ServerSettings settings)
    {
        _allowed = _builtInOrigins
            .Append(settings.PublicBaseUrl)
            .Concat(settings.ExtraAllowedOrigins)
            .Select(Normalize)
            .OfType<string>()
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        var normalized = Normalize(origin);
        if (normalized is null)
        {
            return false;
        }

        // Local tools such as the MCP Inspector run on a loopback address with a random port.
        return _allowed.Contains(normalized) || (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.IsLoopback);
    }

    /// <summary>The scheme://host[:port] of an origin, or null when the value is not a plain origin.</summary>
    private static string? Normalize(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            || uri.UserInfo.Length > 0
            || !string.Equals(uri.PathAndQuery, "/", StringComparison.Ordinal)
            || uri.Fragment.Length > 0)
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
