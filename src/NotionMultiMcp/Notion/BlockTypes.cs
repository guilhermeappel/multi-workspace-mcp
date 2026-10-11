namespace NotionMultiMcp.Notion;

/// <summary>
/// The Notion block types the server reads or writes. Notion may add types at any time, so these stay strings
/// (an enum would have to throw on a value it has never seen) and every switch keeps a fallback arm.
/// </summary>
internal static class BlockTypes
{
    public const string Paragraph = "paragraph";
    public const string Heading1 = "heading_1";
    public const string Heading2 = "heading_2";
    public const string Heading3 = "heading_3";
    public const string BulletedListItem = "bulleted_list_item";
    public const string NumberedListItem = "numbered_list_item";
    public const string ToDo = "to_do";
    public const string Toggle = "toggle";
    public const string Quote = "quote";
    public const string Callout = "callout";
    public const string Code = "code";
    public const string Equation = "equation";
    public const string Divider = "divider";
    public const string Table = "table";
    public const string TableRow = "table_row";
    public const string TableOfContents = "table_of_contents";
    public const string Breadcrumb = "breadcrumb";
    public const string ColumnList = "column_list";
    public const string Column = "column";
    public const string SyncedBlock = "synced_block";
    public const string ChildPage = "child_page";
    public const string ChildDatabase = "child_database";
    public const string LinkToPage = "link_to_page";
    public const string Image = "image";
    public const string Video = "video";
    public const string File = "file";
    public const string Pdf = "pdf";
    public const string Audio = "audio";
    public const string Bookmark = "bookmark";
    public const string Embed = "embed";
    public const string LinkPreview = "link_preview";

    /// <summary>The heading block type for a Markdown heading level; Notion has three, so deeper ones are capped by the caller.</summary>
    public static string Heading(int level) => level switch
    {
        1 => Heading1,
        2 => Heading2,
        3 => Heading3,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Notion headings go from level 1 to 3."),
    };
}
