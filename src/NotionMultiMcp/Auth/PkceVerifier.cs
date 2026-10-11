using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace NotionMultiMcp.Auth;

/// <summary>PKCE with S256 (RFC 7636): the challenge is the base64url of a SHA-256 hash of the verifier.</summary>
internal static class PkceVerifier
{
    private const int ChallengeLength = 43;
    private const int MinVerifierLength = 43;
    private const int MaxVerifierLength = 128;

    private static readonly SearchValues<char> _base64UrlChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private static readonly SearchValues<char> _verifierChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~");

    public static bool IsValidChallenge(string value) =>
        value.Length == ChallengeLength && value.AsSpan().IndexOfAnyExcept(_base64UrlChars) < 0;

    public static bool Matches(string verifier, string challenge)
    {
        if (verifier.Length is < MinVerifierLength or > MaxVerifierLength || verifier.AsSpan().IndexOfAnyExcept(_verifierChars) >= 0)
        {
            return false;
        }

        var computed = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }
}
