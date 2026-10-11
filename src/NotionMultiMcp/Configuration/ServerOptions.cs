namespace NotionMultiMcp.Configuration;

/// <summary>
/// The "Server" configuration section. The defaults below are repeated in appsettings.json (a test keeps them
/// equal); the two secrets are never stored there: use user-secrets in development and environment variables
/// (<c>Server__SigningKey</c>) in production.
/// </summary>
internal sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>The address Claude reaches the server at. https, or http on loopback for local testing.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>The password typed on the authorization page. At least 16 characters.</summary>
    public string? AuthPassword { get; set; }

    /// <summary>The HMAC key as 64 hex characters or base64 (at least 32 bytes).</summary>
    public string? SigningKey { get; set; }

    /// <summary>Turns authentication off, to test with curl or the Inspector. Only honoured in Development.</summary>
    public bool AuthDisabled { get; set; }

    /// <summary>Increment it to invalidate every access and refresh token already issued.</summary>
    public int TokenGeneration { get; set; } = 1;

    /// <summary>Redirect URIs accepted besides the built-in ones (see <see cref="Auth.RedirectPolicy"/>).</summary>
    public string[] ExtraRedirectUris { get; set; } = [];

    /// <summary>Browser origins allowed to call /mcp besides the built-in ones (see <see cref="Mcp.OriginPolicy"/>).</summary>
    public string[] ExtraAllowedOrigins { get; set; } = [];

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>A refresh token slides forward every time it is used, but never past <see cref="MaxSessionLifetime"/>.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(90);

    public TimeSpan AuthorizationCodeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A session started by a login ends after this long, however often its refresh token is used.</summary>
    public TimeSpan MaxSessionLifetime { get; set; } = TimeSpan.FromDays(180);
}
