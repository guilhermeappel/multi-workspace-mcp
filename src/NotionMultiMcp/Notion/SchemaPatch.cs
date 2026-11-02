using System.Text.Json.Nodes;
using NotionMultiMcp.Mcp.Tools;

namespace NotionMultiMcp.Notion;

/// <summary>The <c>properties</c> object of a data source update, and what the tool tells the model about it.</summary>
internal sealed record SchemaChange(JsonObject Properties, IReadOnlyList<string> Notes);

/// <summary>Builds the schema part of the Notion requests behind update_data_source and create_database.</summary>
internal static class SchemaPatch
{
    /// <summary>The column types the tools can create or convert to. Their settings are sent empty.</summary>
    public static readonly IReadOnlyList<string> SettableTypes =
        [PropertyTypes.Number, PropertyTypes.RichText, PropertyTypes.Select, PropertyTypes.MultiSelect, PropertyTypes.Date, PropertyTypes.Checkbox, PropertyTypes.Url];

    private const string OptionsKey = "options";

    /// <summary>
    /// Turns the arguments of update_data_source into the properties patch. <paramref name="current"/> is the
    /// <c>properties</c> object of the data source as it is now; every column is looked up in it first, so a
    /// mistake is reported before anything is sent to Notion.
    /// </summary>
    public static SchemaChange Build(
        JsonObject current,
        JsonObject? rename,
        JsonObject? changeTypes,
        JsonObject? selectOptions,
        JsonObject? add,
        JsonArray? remove)
    {
        var patch = new JsonObject();
        var notes = new List<string>();
        var removed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in remove ?? [])
        {
            var column = Existing(current, J.Str(node) ?? "", "remove_properties");
            RefuseTitle(current, column, "removed");
            removed.Add(column);
            patch[column] = null;
        }

        foreach (var (key, value) in rename ?? [])
        {
            var column = Existing(current, key, "rename_properties");
            var newName = Text(value, "rename_properties", key);
            NotRemoved(removed, column);
            if (!string.Equals(column, newName, StringComparison.Ordinal))
            {
                EnsureFree(current, rename, add, column, newName);
                Entry(patch, column)["name"] = newName;
            }
        }

        var typeAfter = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in changeTypes ?? [])
        {
            var column = Existing(current, key, "change_property_types");
            var type = SettableType(Text(value, "change_property_types", key), "change_property_types");
            NotRemoved(removed, column);
            RefuseTitle(current, column, "converted");
            if (string.Equals(J.Str(current[column]?["type"]), type, StringComparison.Ordinal))
            {
                notes.Add($"\"{column}\" is already of type {type}; left as it is.");
                continue;
            }

            typeAfter[column] = type;
            Entry(patch, column)[type] = new JsonObject();
        }

        foreach (var (key, value) in selectOptions ?? [])
        {
            var column = Existing(current, key, "set_select_options");
            NotRemoved(removed, column);
            ApplyOptions(current, patch, notes, typeAfter, column, J.ArrayArg(value)
                ?? throw new ToolException($"set_select_options: the value for \"{column}\" must be a list of option names."));
        }

        foreach (var (name, value) in add ?? [])
        {
            var type = SettableType(Text(value, "add_properties", name), "add_properties");
            if (Find(current, name) is not null)
            {
                throw new ToolException($"add_properties: a column named \"{name}\" already exists.");
            }

            Entry(patch, name)[type] = new JsonObject();
        }

        return new SchemaChange(patch, notes);
    }

    /// <summary>
    /// The initial properties of create_database: each value is a type name or, for select and multi_select,
    /// an object such as <c>{"select":["A","B"]}</c>. There must be exactly one title column.
    /// </summary>
    public static JsonObject BuildNew(JsonObject? input)
    {
        if (input is null or { Count: 0 })
        {
            throw new ToolException("Pass properties with exactly one column of type \"title\", for example {\"Name\":\"title\",\"Status\":{\"select\":[\"Todo\",\"Done\"]}}.");
        }

        var result = new JsonObject();
        var titles = 0;
        foreach (var (name, value) in input)
        {
            if (value is JsonObject { Count: 1 } configured)
            {
                var (type, options) = configured.Single();
                result[name] = NewSelect(name, type, J.ArrayArg(options));
                continue;
            }

            var typeName = Text(value, "properties", name);
            if (string.Equals(typeName, PropertyTypes.Title, StringComparison.Ordinal))
            {
                titles++;
                result[name] = new JsonObject { [PropertyTypes.Title] = new JsonObject() };
            }
            else
            {
                result[name] = new JsonObject { [SettableType(typeName, "properties")] = new JsonObject() };
            }
        }

        if (titles != 1)
        {
            throw new ToolException($"properties needs exactly one column of type \"title\"; found {titles}.");
        }

        return result;
    }

    private static JsonObject NewSelect(string column, string type, JsonArray? options)
    {
        if (type is not (PropertyTypes.Select or PropertyTypes.MultiSelect) || options is null)
        {
            throw new ToolException(
                $"properties: the value for \"{column}\" must be a type name, or {{\"select\":[\"A\",\"B\"]}} / {{\"multi_select\":[\"A\",\"B\"]}}.");
        }

        var names = options.Select(o => J.Str(o)?.Trim() ?? "").Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        var list = new JsonArray(names.Select(n => (JsonNode)new JsonObject { ["name"] = n }).ToArray());
        return new JsonObject { [type] = new JsonObject { [OptionsKey] = list } };
    }

    /// <summary>
    /// Replaces the options of a select or multi-select column. An option that stays (same name, ignoring case)
    /// is sent with its id and color, so Notion keeps it and the rows that use it; the others are added or dropped.
    /// </summary>
    private static void ApplyOptions(
        JsonObject current,
        JsonObject patch,
        List<string> notes,
        Dictionary<string, string> typeAfter,
        string column,
        JsonArray wanted)
    {
        var currentType = J.Str(current[column]?["type"]) ?? "?";
        var type = typeAfter.GetValueOrDefault(column, currentType);
        if (type is not (PropertyTypes.Select or PropertyTypes.MultiSelect or PropertyTypes.Status))
        {
            throw new ToolException($"set_select_options: \"{column}\" is of type {type}, not select, multi_select or status.");
        }

        var existing = string.Equals(currentType, type, StringComparison.Ordinal)
            ? J.Arr(current[column]?[type]?[OptionsKey]).OfType<JsonObject>().ToList()
            : [];
        var names = wanted.Select(o => J.Str(o)?.Trim() ?? "").Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var options = new JsonArray();
        var added = new List<string>();
        foreach (var name in names)
        {
            var match = existing.FirstOrDefault(o => string.Equals(J.Str(o["name"])?.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                options.Add(new JsonObject { ["name"] = name });
                added.Add(name);
                continue;
            }

            var kept = new JsonObject { ["id"] = J.Str(match["id"]) };
            if (J.Str(match["color"]) is { } color)
            {
                kept["color"] = color;
            }

            options.Add(kept);
        }

        var dropped = existing
            .Select(o => J.Str(o["name"]) ?? "")
            .Where(n => !names.Contains(n.Trim(), StringComparer.OrdinalIgnoreCase))
            .ToList();

        Entry(patch, column)[type] = new JsonObject { [OptionsKey] = options };
        if (added.Count > 0)
        {
            notes.Add($"\"{column}\": options added: {string.Join(", ", added)}.");
        }

        if (dropped.Count > 0)
        {
            notes.Add($"\"{column}\": options removed (rows that had them lose the value): {string.Join(", ", dropped)}.");
        }

        if (added.Count > 0 && dropped.Count > 0)
        {
            notes.Add(
                $"\"{column}\": Notion's API cannot rename an option, so a new name is a new option and the old one is dropped. " +
                "Rename it in Notion itself if the rows must keep their value.");
        }
    }

    private static JsonObject Entry(JsonObject patch, string column)
    {
        if (patch[column] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        patch[column] = created;
        return created;
    }

    private static string? Find(JsonObject current, string name) =>
        current.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.Ordinal))
        ?? current.Select(p => p.Key).FirstOrDefault(k => string.Equals(k.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Existing(JsonObject current, string name, string argument) =>
        Find(current, name)
        ?? throw new ToolException(
            $"{argument}: there is no column named \"{name}\". Columns: " +
            string.Join(", ", current.Select(p => $"{p.Key} ({J.Str(p.Value?["type"])})")));

    private static void RefuseTitle(JsonObject current, string column, string verb)
    {
        if (string.Equals(J.Str(current[column]?["type"]), PropertyTypes.Title, StringComparison.Ordinal))
        {
            throw new ToolException($"\"{column}\" is the title column; it cannot be {verb}. (It can be renamed.)");
        }
    }

    private static void NotRemoved(HashSet<string> removed, string column)
    {
        if (removed.Contains(column))
        {
            throw new ToolException($"\"{column}\" is in remove_properties, so it cannot be changed in the same call.");
        }
    }

    private static void EnsureFree(JsonObject current, JsonObject? rename, JsonObject? add, string column, string newName)
    {
        var taken = current.Any(p => !string.Equals(p.Key, column, StringComparison.Ordinal) && string.Equals(p.Key.Trim(), newName.Trim(), StringComparison.OrdinalIgnoreCase))
            || (add ?? []).Any(p => string.Equals(p.Key.Trim(), newName.Trim(), StringComparison.OrdinalIgnoreCase))
            || (rename ?? []).Count(p => string.Equals(J.Str(p.Value)?.Trim(), newName.Trim(), StringComparison.OrdinalIgnoreCase)) > 1;
        if (taken)
        {
            throw new ToolException($"rename_properties: the name \"{newName}\" is already used by another column.");
        }
    }

    private static string Text(JsonNode? value, string argument, string column) =>
        J.Str(value) is { Length: > 0 } text && text.Trim().Length > 0
            ? text.Trim()
            : throw new ToolException($"{argument}: the value for \"{column}\" must be a non-empty string.");

    private static string SettableType(string type, string argument) =>
        SettableTypes.Contains(type, StringComparer.Ordinal)
            ? type
            : throw new ToolException($"{argument}: type \"{type}\" is not supported. Use one of: {string.Join(", ", SettableTypes)}.");
}
