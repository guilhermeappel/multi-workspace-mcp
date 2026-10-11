using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace NotionMultiMcp.Auth;

/// <summary>
/// Stateless tokens: JSON payload + HMAC-SHA256. Nothing is kept in a database or in memory,
/// so the container can scale to zero and come back without dropping Claude's connection.
/// Changing Server:SigningKey (or incrementing Server:TokenGeneration) invalidates everything already issued.
/// </summary>
internal sealed class TokenService(ServerSettings settings, TimeProvider timeProvider)
{
    private const int MaxTokenLength = 8192;

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Current time as Unix seconds, the unit used by every token expiry.</summary>
    public long Now => timeProvider.GetUtcNow().ToUnixTimeSeconds();

    public string Protect<T>(TokenPurpose purpose, T payload)
    {
        var body = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions));
        return body + "." + Sign(purpose, body);
    }

    public bool TryUnprotect<T>(TokenPurpose purpose, string? token, [NotNullWhen(true)] out T? payload)
        where T : class
    {
        payload = null;
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return false;
        }

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
        {
            return false;
        }

        var body = token[..dot];
        var signature = token[(dot + 1)..];
        var expected = Sign(purpose, body);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(expected)))
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<T>(WebEncoders.Base64UrlDecode(body), _jsonOptions);
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return false;
        }

        return payload is not null;
    }

    public bool IsValidAccessToken(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader)
            || !authorizationHeader.StartsWith(OAuthProtocol.BearerScheme + " ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = authorizationHeader[(OAuthProtocol.BearerScheme.Length + 1)..].Trim();
        return TryUnprotect<AccessToken>(TokenPurpose.Access, token, out var access)
            && access.ExpiresAt > Now
            && access.Generation == settings.TokenGeneration;
    }

    public bool PasswordMatches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(settings.AuthPassword))
        {
            return false;
        }

        var a = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(settings.AuthPassword));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static string Hash(string value) =>
        WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string NewNonce() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// The text that goes into the signature. These exact strings are what tokens issued by earlier releases were
    /// signed with, so renaming a member must not change them.
    /// </summary>
    private static string PurposeName(TokenPurpose purpose) => purpose switch
    {
        TokenPurpose.Client => "client",
        TokenPurpose.Code => "code",
        TokenPurpose.Access => "access",
        TokenPurpose.Refresh => "refresh",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown token purpose."),
    };

    private string Sign(TokenPurpose purpose, string body)
    {
        var data = Encoding.UTF8.GetBytes(PurposeName(purpose) + "|" + body);
        return WebEncoders.Base64UrlEncode(HMACSHA256.HashData(settings.SigningKey, data));
    }
}

/// <summary>Client registration (DCR). The client_id itself is this signed registration.</summary>
internal sealed record ClientRegistration(string[] RedirectUris, string? Name, long IssuedAt);

internal sealed record AuthorizationCode(string ClientHash, string RedirectUri, string CodeChallenge, long ExpiresAt, string Nonce);

internal sealed record AccessToken(long ExpiresAt, int Generation);

internal sealed record RefreshToken(string ClientHash, long ExpiresAt, int Generation, string Nonce, long SessionStartedAt);
