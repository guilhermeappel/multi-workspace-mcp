using System.Text.Json.Nodes;

namespace NotionMultiMcp.Notion;

/// <summary>Reading (value -> text) and writing (simple value -> Notion format) of properties.</summary>
internal static class Properties
{
    private const int MaxListedRelations = 10;

    public static string PageTitle(JsonNode? page)
    {
        foreach (var (_, property) in page?["properties"] as JsonObject ?? [])
        {
            if (string.Equals(J.Str(property?["type"]), PropertyTypes.Title, StringComparison.Ordinal))
            {
                var title = RichText.Plain(property?["title"]);
                return title.Length > 0 ? title : "(untitled)";
            }
        }

        return "(untitled)";
    }

    /// <summary>The properties of a page or row as name and value pairs, without the title and without empty values.</summary>
    public static IEnumerable<(string Name, string Value)> Readable(JsonNode? properties) =>
        (properties as JsonObject ?? [])
            .Where(p => !string.Equals(J.Str(p.Value?["type"]), PropertyTypes.Title, StringComparison.Ordinal))
            .Select(p => (Name: p.Key, Value: Format(p.Value)))
            .Where(p => p.Value.Length > 0);

    public static string Format(JsonNode? property)
    {
        var type = J.Str(property?["type"]);
        if (type is null)
        {
            return "";
        }

        var value = property?[type];
        return type switch
        {
            "title" or "rich_text" => RichText.Plain(value),
            "number" => value?.ToJsonString() ?? "",
            "select" or "status" => J.Str(value?["name"]) ?? "",
            "multi_select" => string.Join(", ", J.Arr(value).Select(o => J.Str(o?["name"]))),
            "date" => FormatDate(value),
            "checkbox" => J.Bool(value) ? "yes" : "no",
            "url" or "email" or "phone_number" => J.Str(value) ?? "",
            "people" => string.Join(", ", J.Arr(value).Select(p => J.Str(p?["name"]) ?? J.Str(p?["id"]))),
            "relation" => FormatRelation(J.Arr(value)),
            "formula" => FormatTyped(value),
            "rollup" => FormatRollup(value),
            "created_time" or "last_edited_time" => J.Str(value) ?? "",
            "created_by" or "last_edited_by" => J.Str(value?["name"]) ?? J.Str(value?["id"]) ?? "",
            "files" => string.Join(", ", J.Arr(value).Select(f => J.Str(f?["name"]))),
            "unique_id" => $"{J.Str(value?["prefix"])}{(J.Str(value?["prefix"]) is null ? "" : "-")}{value?["number"]?.ToJsonString()}",
            "verification" => J.Str(value?["state"]) ?? "",
            _ => $"[unsupported property type: {type}]",
        };
    }

    /// <summary>Name -> type, from the properties of a data source or a page.</summary>
    public static Dictionary<string, string> SchemaTypes(JsonNode? properties)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, property) in properties as JsonObject ?? [])
        {
            if (J.Str(property?["type"]) is { } type)
            {
                result[name] = type;
            }
        }

        return result;
    }

    public static string TitlePropertyName(Dictionary<string, string> schema) =>
        schema.FirstOrDefault(kv => string.Equals(kv.Value, PropertyTypes.Title, StringComparison.Ordinal)).Key
        ?? throw new ToolException("This data source has no title property.");

    public static JsonObject TitleValue(string title) => new() { ["title"] = RichText.FromPlain(title) };

    /// <summary>
    /// Converts {"Status": "Done", "Tags": ["a","b"], "Due": "2026-10-01"} to the Notion format,
    /// using each property's type. Objects already in the Notion format pass through.
    /// </summary>
    public static JsonObject Build(Dictionary<string, string> schema, JsonObject? input)
    {
        var result = new JsonObject();
        if (input is null)
        {
            return result;
        }

        foreach (var (name, value) in input)
        {
            if (!schema.TryGetValue(name, out var type))
            {
                var available = string.Join(", ", schema.Select(kv => $"{kv.Key} ({kv.Value})"));
                throw new ToolException($"Unknown property '{name}'. Available: {available}");
            }

            result[name] = Convert(name, type, value);
        }

        return result;
    }

    private static JsonNode Convert(string name, string type, JsonNode? value)
    {
        if (value is JsonObject raw && raw.ContainsKey(type))
        {
            return raw.DeepClone();
        }

        var text = value is JsonValue ? J.Str(value) ?? value.ToJsonString() : null;
        switch (type)
        {
            case PropertyTypes.Title:
            case PropertyTypes.RichText:
                return new JsonObject { [type] = RichText.FromPlain(text ?? "") };
            case PropertyTypes.Number:
                return new JsonObject { [PropertyTypes.Number] = NumberValue(name, value, text) };
            case PropertyTypes.Select:
            case PropertyTypes.Status:
                return new JsonObject { [type] = string.IsNullOrEmpty(text) ? null : new JsonObject { ["name"] = text } };
            case PropertyTypes.MultiSelect:
                return new JsonObject { [PropertyTypes.MultiSelect] = NamedArray(StringList(value), "name") };
            case PropertyTypes.Date:
                if (value is JsonObject range)
                {
                    return new JsonObject { [PropertyTypes.Date] = range.DeepClone() };
                }

                return new JsonObject { [PropertyTypes.Date] = string.IsNullOrEmpty(text) ? null : new JsonObject { ["start"] = text } };
            case PropertyTypes.Checkbox:
                return new JsonObject { [PropertyTypes.Checkbox] = CheckboxValue(name, value, text) };
            case PropertyTypes.Url:
            case PropertyTypes.Email:
            case PropertyTypes.PhoneNumber:
                return new JsonObject { [type] = string.IsNullOrEmpty(text) ? null : text };
            case PropertyTypes.People:
                return new JsonObject { [PropertyTypes.People] = NamedArray(StringList(value), "id", withObject: "user") };
            case PropertyTypes.Relation:
                return new JsonObject { [PropertyTypes.Relation] = NamedArray(StringList(value).Select(NotionIds.Parse), "id") };
            default:
                throw new ToolException(
                    $"Property '{name}' has type '{type}', which can't be set from a simple value. " +
                    $"Pass the raw Notion format instead, e.g. {{\"{type}\": ...}}, or leave it out.");
        }
    }

    /// <summary>A missing or blank value clears the number; anything else must parse, never clear silently.</summary>
    private static double? NumberValue(string name, JsonNode? value, string? text)
    {
        if (value is null || (value is JsonValue && string.IsNullOrWhiteSpace(text)))
        {
            return null;
        }

        return J.Number(value)
            ?? throw new ToolException($"Property '{name}' expects a number, got '{text ?? value.ToJsonString()}'.");
    }

    private static bool CheckboxValue(string name, JsonNode? value, string? text)
    {
        if (value is null)
        {
            return false;
        }

        return J.BoolOrNull(value)
            ?? throw new ToolException($"Property '{name}' expects true or false, got '{text ?? value.ToJsonString()}'.");
    }

    private static IEnumerable<string> StringList(JsonNode? value)
    {
        if (value is JsonArray array)
        {
            return array.Select(J.Str).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
        }

        var text = J.Str(value);
        return string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static JsonArray NamedArray(IEnumerable<string> values, string key, string? withObject = null)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            var item = new JsonObject();
            if (withObject is not null)
            {
                item["object"] = withObject;
            }

            item[key] = value;
            array.Add(item);
        }

        return array;
    }

    private static string FormatDate(JsonNode? value)
    {
        var start = J.Str(value?["start"]);
        var end = J.Str(value?["end"]);
        return start is null ? "" : end is null ? start : $"{start} → {end}";
    }

    private static string FormatRelation(JsonArray relations)
    {
        var ids = relations.Select(r => J.Str(r?["id"])).Where(id => id is not null).ToList();
        return ids.Count <= MaxListedRelations
            ? string.Join(", ", ids)
            : string.Join(", ", ids.Take(MaxListedRelations)) + $" … (+{ids.Count - MaxListedRelations})";
    }

    private static string FormatTyped(JsonNode? value)
    {
        var kind = J.Str(value?["type"]);
        if (kind is null)
        {
            return "";
        }

        var inner = value?[kind];
        return kind switch
        {
            "string" => J.Str(inner) ?? "",
            "boolean" => J.Bool(inner) ? "yes" : "no",
            "date" => FormatDate(inner),
            _ => inner?.ToJsonString() ?? "",
        };
    }

    private static string FormatRollup(JsonNode? value)
    {
        var kind = J.Str(value?["type"]);
        return kind switch
        {
            "array" => string.Join(", ", J.Arr(value?["array"]).Select(Format).Where(s => s.Length > 0)),
            "date" => FormatDate(value?["date"]),
            null => "",
            _ => value?[kind]?.ToJsonString() ?? "",
        };
    }
}
