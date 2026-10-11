using Microsoft.Extensions.Options;

namespace NotionMultiMcp.Configuration;

/// <summary>
/// Checks everything at once, so a deployment with three mistakes reports three lines instead of three
/// restarts.
/// </summary>
internal sealed class ServerOptionsValidator(IHostEnvironment environment) : IValidateOptions<ServerOptions>
{
    private const int MinPasswordLength = 16;

    public ValidateOptionsResult Validate(string? name, ServerOptions options)
    {
        var failures = new List<string>();
        var authDisabled = ServerSettings.IsAuthDisabled(options, environment);

        if (!Uri.TryCreate(options.PublicBaseUrl?.Trim(), UriKind.Absolute, out var uri) || !IsAllowedBaseUrl(uri))
        {
            failures.Add("Server:PublicBaseUrl must be an https URL (or http://localhost for local testing).");
        }

        // Without auth there is nothing to protect, so a throwaway key is generated and none is required.
        var keyIsOptional = authDisabled && string.IsNullOrWhiteSpace(options.SigningKey);
        if (!keyIsOptional && !SigningKeyFormat.IsStrong(SigningKeyFormat.Parse(options.SigningKey)))
        {
            failures.Add("Server:SigningKey must have at least 32 random bytes (64 hex characters or base64).");
        }

        if (!authDisabled && (string.IsNullOrWhiteSpace(options.AuthPassword) || options.AuthPassword.Length < MinPasswordLength))
        {
            failures.Add("Server:AuthPassword is required and must have at least 16 characters.");
        }

        // Revocation depends on this number, so it must be a real generation and not a leftover default.
        if (options.TokenGeneration < 1)
        {
            failures.Add("Server:TokenGeneration must be a positive whole number (for example 2).");
        }

        RequirePositive(failures, nameof(ServerOptions.AccessTokenLifetime), options.AccessTokenLifetime);
        RequirePositive(failures, nameof(ServerOptions.RefreshTokenLifetime), options.RefreshTokenLifetime);
        RequirePositive(failures, nameof(ServerOptions.AuthorizationCodeLifetime), options.AuthorizationCodeLifetime);
        RequirePositive(failures, nameof(ServerOptions.MaxSessionLifetime), options.MaxSessionLifetime);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // Check the scheme explicitly: on Linux "/path" parses as an absolute file: URI, and file: URIs count as loopback.
    private static bool IsAllowedBaseUrl(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
        || (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && uri.IsLoopback);

    private static void RequirePositive(List<string> failures, string property, TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"Server:{property} must be a positive duration (for example 01:00:00).");
        }
    }
}
