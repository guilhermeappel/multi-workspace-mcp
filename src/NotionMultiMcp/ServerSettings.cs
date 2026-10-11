using NotionMultiMcp.Configuration;

namespace NotionMultiMcp;

/// <summary>
/// The validated, ready-to-use form of <see cref="ServerOptions"/>: the base URL is normalised and the signing
/// key is decoded. Build it with <see cref="From"/> only after the options passed validation.
/// </summary>
internal sealed class ServerSettings
{
    public required string PublicBaseUrl { get; init; }
    public required string? AuthPassword { get; init; }
    public required byte[] SigningKey { get; init; }
    public required bool AuthDisabled { get; init; }
    public required int TokenGeneration { get; init; }
    public required IReadOnlyList<string> ExtraRedirectUris { get; init; }

    /// <summary>Browser origins allowed to call /mcp in addition to the built-in ones (see <see cref="Mcp.OriginPolicy"/>).</summary>
    public required IReadOnlyList<string> ExtraAllowedOrigins { get; init; }

    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(90);
    public TimeSpan AuthorizationCodeLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A session started by a login ends after this long, however often its refresh token is used.</summary>
    public TimeSpan MaxSessionLifetime { get; init; } = TimeSpan.FromDays(180);

    /// <summary>The exact URL you paste in Claude when adding the connector.</summary>
    public string McpUrl => PublicBaseUrl + "/mcp";

    /// <summary>True when a resource indicator (RFC 8707) names this server: its MCP URL or its base URL.</summary>
    public bool IsOwnResource(string resource)
    {
        var normalized = resource.TrimEnd('/');
        return string.Equals(normalized, McpUrl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, PublicBaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>AuthDisabled only applies in Development, to test with curl or the Inspector without OAuth.</summary>
    public static bool IsAuthDisabled(ServerOptions options, IHostEnvironment environment) =>
        options.AuthDisabled && environment.IsDevelopment();

    public static ServerSettings From(ServerOptions options, IHostEnvironment environment)
    {
        var authDisabled = IsAuthDisabled(options, environment);

        // Without auth there is nothing to protect, so a throwaway key is fine and none is required.
        var key = authDisabled && string.IsNullOrWhiteSpace(options.SigningKey)
            ? SigningKeyFormat.Generate()
            : SigningKeyFormat.Parse(options.SigningKey);

        return new ServerSettings
        {
            PublicBaseUrl = options.PublicBaseUrl.Trim().TrimEnd('/'),
            AuthPassword = options.AuthPassword,
            SigningKey = key,
            AuthDisabled = authDisabled,
            TokenGeneration = options.TokenGeneration,
            ExtraRedirectUris = Clean(options.ExtraRedirectUris),
            ExtraAllowedOrigins = Clean(options.ExtraAllowedOrigins),
            AccessTokenLifetime = options.AccessTokenLifetime,
            RefreshTokenLifetime = options.RefreshTokenLifetime,
            AuthorizationCodeLifetime = options.AuthorizationCodeLifetime,
            MaxSessionLifetime = options.MaxSessionLifetime,
        };
    }

    private static string[] Clean(string[] values) =>
        [.. values.Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v))!];
}
