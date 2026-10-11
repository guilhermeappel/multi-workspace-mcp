namespace NotionMultiMcp.Configuration;

/// <summary>Reading and judging the HMAC key given as hex or base64.</summary>
internal static class SigningKeyFormat
{
    public const int MinBytes = 32;
    private const int MinDistinctBytes = 12;

    /// <summary>The key bytes, or an empty array when the value is missing or is neither hex nor base64.</summary>
    public static byte[] Parse(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        if (value.Length % 2 == 0 && value.All(Uri.IsHexDigit))
        {
            return Convert.FromHexString(value);
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    /// <summary>Long enough and not trivially repetitive ("aaaa…" or "0000…").</summary>
    public static bool IsStrong(byte[] key) => key.Length >= MinBytes && key.Distinct().Count() >= MinDistinctBytes;

    public static byte[] Generate() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(MinBytes);
}
