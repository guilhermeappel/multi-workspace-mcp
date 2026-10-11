namespace NotionMultiMcp.Configuration;

/// <summary>Reading and judging the HMAC key given as hex or base64.</summary>
internal static class SigningKeyFormat
{
    public const int MinBytes = 32;
    private const int MinDistinctBytes = 12;
    private const int MinDistinctBase64Characters = 20;

    /// <summary>The key bytes, or an empty array when the value is missing or is neither hex nor base64.</summary>
    public static byte[] Parse(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        if (IsHex(value))
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

    /// <summary>
    /// Catches the common mistake of a passphrase that happens to be valid base64: it decodes to bytes that look random,
    /// but its guessing cost is that of the phrase. 32 random bytes in base64 use about 32 different characters (fewer
    /// than 20 happens for about one random key in 50 million), and text such as a repeated phrase uses far fewer. It is a safety net, not a proof
    /// of randomness: generate the key with `openssl rand -hex 32`. Hex is covered by the byte-level test.
    /// </summary>
    public static bool LooksRandom(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || IsHex(value))
        {
            return true;
        }

        return value.TrimEnd('=').Distinct().Count() >= MinDistinctBase64Characters;
    }

    public static byte[] Generate() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(MinBytes);

    private static bool IsHex(string value) => value.Length % 2 == 0 && value.All(Uri.IsHexDigit);
}
