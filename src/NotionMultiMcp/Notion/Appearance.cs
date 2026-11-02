using System.Text.Json.Nodes;
using NotionMultiMcp.Mcp.Tools;

namespace NotionMultiMcp.Notion;

/// <summary>
/// The icon and cover arguments of the tools, turned into the Notion format. A change with a null
/// <see cref="Value"/> removes the icon or the cover; "no change" is the absence of a change.
/// </summary>
internal sealed record AppearanceChange(JsonNode? Value);

internal static class Appearance
{
    private const string None = "none";

    /// <summary>An emoji becomes an emoji icon, a URL an external icon and "none" removes it. Blank means no change.</summary>
    public static AppearanceChange? Icon(string? input)
    {
        if (Normalize(input) is not { } text)
        {
            return null;
        }

        if (IsNone(text))
        {
            return new AppearanceChange(null);
        }

        if (IsUrl(text))
        {
            return new AppearanceChange(External(text));
        }

        if (text.Any(char.IsAsciiLetter))
        {
            throw new ToolException("icon must be an emoji (for example ⭐), an image URL starting with http, or \"none\" to remove it.");
        }

        return new AppearanceChange(new JsonObject { ["type"] = "emoji", ["emoji"] = text });
    }

    /// <summary>An external image URL, or "none" to remove the cover. Blank means no change.</summary>
    public static AppearanceChange? Cover(string? input)
    {
        if (Normalize(input) is not { } text)
        {
            return null;
        }

        if (IsNone(text))
        {
            return new AppearanceChange(null);
        }

        if (!IsUrl(text))
        {
            throw new ToolException("cover must be an image URL starting with http, or \"none\" to remove it.");
        }

        return new AppearanceChange(External(text));
    }

    /// <summary>Puts the icon and cover that were asked for into a request body; the arguments that were not passed are left out.</summary>
    public static void Apply(JsonObject body, AppearanceChange? icon, AppearanceChange? cover)
    {
        if (icon is not null)
        {
            body["icon"] = icon.Value?.DeepClone();
        }

        if (cover is not null)
        {
            body["cover"] = cover.Value?.DeepClone();
        }
    }

    /// <summary>The icon of a Notion object as short text, or null when there is none.</summary>
    public static string? Describe(JsonNode? icon)
    {
        return J.Str(icon?["type"]) switch
        {
            "emoji" => J.Str(icon?["emoji"]),
            "external" => J.Str(icon?["external"]?["url"]),
            "file" or "file_upload" => "(uploaded image)",
            "custom_emoji" => ":" + J.Str(icon?["custom_emoji"]?["name"]) + ":",
            "icon" => "Notion icon " + J.Str(icon?["icon"]?["name"]),
            _ => null,
        };
    }

    private static string? Normalize(string? input) => string.IsNullOrWhiteSpace(input) ? null : input.Trim();

    private static bool IsNone(string text) => string.Equals(text, None, StringComparison.OrdinalIgnoreCase);

    private static bool IsUrl(string text) =>
        text.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static JsonObject External(string url) =>
        new() { ["type"] = "external", ["external"] = new JsonObject { ["url"] = url } };
}
