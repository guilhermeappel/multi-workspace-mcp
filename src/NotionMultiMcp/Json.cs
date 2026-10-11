using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotionMultiMcp;

/// <summary>Tolerant helpers to read JsonNode (models sometimes send numbers/objects as strings).</summary>
internal static class J
{
    public static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    public static bool Bool(JsonNode? node) => BoolOrNull(node) ?? false;

    public static bool? BoolOrNull(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var b))
        {
            return b;
        }

        return value.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed) ? parsed : null;
    }

    public static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return (int)d;
        }

        return value.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)
            ? i
            : null;
    }

    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        return value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)
            ? d
            : null;
    }

    /// <summary>
    /// Parses a JSON body, telling invalid JSON apart from the valid literal <c>null</c>. Returning a result
    /// (instead of throwing) keeps the callers free of a try/catch around a variable declared before it.
    /// </summary>
    public static async Task<JsonParseResult> TryParseAsync(Stream body, CancellationToken cancellationToken)
    {
        try
        {
            return new JsonParseResult(true, await JsonNode.ParseAsync(body, cancellationToken: cancellationToken));
        }
        catch (JsonException)
        {
            return new JsonParseResult(false, null);
        }
    }

    public static JsonArray Arr(JsonNode? node) => node as JsonArray ?? [];

    /// <summary>Accepts a JSON object or a string containing JSON. Returns a detached copy.</summary>
    public static JsonObject? ObjectArg(JsonNode? node) => ParseIfString(node) as JsonObject;

    /// <summary>Accepts a JSON array or a string containing JSON. Returns a detached copy.</summary>
    public static JsonArray? ArrayArg(JsonNode? node) => ParseIfString(node) as JsonArray;

    private static JsonNode? ParseIfString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (Str(node) is { } text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                throw new ToolException("Argument is not valid JSON: " + text);
            }
        }

        return node.DeepClone();
    }
}


internal readonly record struct JsonParseResult(bool IsValid, JsonNode? Node);
