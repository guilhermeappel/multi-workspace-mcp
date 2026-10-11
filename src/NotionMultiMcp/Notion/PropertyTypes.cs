namespace NotionMultiMcp.Notion;

/// <summary>
/// Names of the Notion property types the server recognises. Like <see cref="BlockTypes"/> they stay strings,
/// because Notion adds property types over time and an unknown one is reported, not a crash.
/// </summary>
internal static class PropertyTypes
{
    public const string Title = "title";
    public const string RichText = "rich_text";
    public const string Number = "number";
    public const string Select = "select";
    public const string MultiSelect = "multi_select";
    public const string Status = "status";
    public const string Date = "date";
    public const string Checkbox = "checkbox";
    public const string Url = "url";
    public const string Email = "email";
    public const string PhoneNumber = "phone_number";
    public const string People = "people";
    public const string Relation = "relation";
}
