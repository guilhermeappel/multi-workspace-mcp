using System.Text.Json.Nodes;
using NotionMultiMcp.Mcp.Tools;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Tests;

/// <summary>The payloads behind icons, covers and schema changes, without any HTTP.</summary>
public sealed class AppearanceAndSchemaPatchTests
{
    private static JsonObject Raw(string json) => JsonNode.Parse(json)!.AsObject();

    private const string Current = """
        {"Name":{"id":"title","type":"title","title":{}},
         "Tickets":{"id":"t","type":"url","url":{}},
         "Status":{"id":"s","type":"select","select":{"options":[
            {"id":"id-a","name":"Todo","color":"red"},
            {"id":"id-b","name":"Reviewed","color":"green"},
            {"id":"id-c","name":"Old","color":"gray"}]}}}
        """;

    // ------------------------------------------------------------------ icon and cover

    [Fact]
    public void An_emoji_becomes_an_emoji_icon()
    {
        var icon = Appearance.Icon(" ⭐ ");

        Assert.Equal("""{"type":"emoji","emoji":"⭐"}""", icon!.Value!.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    [Fact]
    public void An_emoji_with_a_variation_selector_is_accepted()
    {
        Assert.Equal("🗺️", (string)Appearance.Icon("🗺️")!.Value!["emoji"]!);
    }

    [Fact]
    public void A_url_becomes_an_external_icon()
    {
        var icon = Appearance.Icon("https://example.com/i.png");

        Assert.Equal("""{"type":"external","external":{"url":"https://example.com/i.png"}}""", icon!.Value!.ToJsonString());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    public void None_removes_the_icon_and_the_cover(string input)
    {
        Assert.Null(Appearance.Icon(input)!.Value);
        Assert.Null(Appearance.Cover(input)!.Value);

        var body = new JsonObject();
        Appearance.Apply(body, Appearance.Icon(input), Appearance.Cover(input));
        Assert.Equal("""{"icon":null,"cover":null}""", body.ToJsonString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_icon_or_cover_means_no_change(string? input)
    {
        Assert.Null(Appearance.Icon(input));
        Assert.Null(Appearance.Cover(input));
        var body = new JsonObject();
        Appearance.Apply(body, Appearance.Icon(input), Appearance.Cover(input));
        Assert.Empty(body);
    }

    [Fact]
    public void A_cover_is_an_external_image()
    {
        Assert.Equal(
            """{"type":"external","external":{"url":"https://example.com/c.jpg"}}""",
            Appearance.Cover("https://example.com/c.jpg")!.Value!.ToJsonString());
    }

    [Theory]
    [InlineData("star")]
    [InlineData("ftp://example.com/x.png")]
    public void Text_that_is_not_an_emoji_or_http_url_is_rejected_for_the_icon(string input) =>
        Assert.Throws<ToolException>(() => Appearance.Icon(input));

    [Theory]
    [InlineData("⭐")]
    [InlineData("example.com/x.png")]
    public void A_cover_must_be_an_http_url(string input) =>
        Assert.Throws<ToolException>(() => Appearance.Cover(input));

    // ------------------------------------------------------------------ schema changes

    [Fact]
    public void Renaming_a_column_sends_only_the_new_name()
    {
        var change = SchemaPatch.Build(Raw(Current), Raw("""{"Tickets":"Tickets count"}"""), null, null, null, null);

        Assert.Equal("""{"Tickets":{"name":"Tickets count"}}""", change.Properties.ToJsonString());
    }

    [Fact]
    public void Changing_a_type_sends_the_empty_configuration_of_the_new_type()
    {
        var change = SchemaPatch.Build(Raw(Current), null, Raw("""{"Tickets":"number"}"""), null, null, null);

        Assert.Equal("""{"Tickets":{"number":{}}}""", change.Properties.ToJsonString());
    }

    [Fact]
    public void Renaming_and_converting_the_same_column_is_one_entry()
    {
        var change = SchemaPatch.Build(Raw(Current), Raw("""{"Tickets":"Count"}"""), Raw("""{"Tickets":"number"}"""), null, null, null);

        Assert.Equal("""{"Tickets":{"name":"Count","number":{}}}""", change.Properties.ToJsonString());
    }

    [Fact]
    public void Converting_to_the_type_a_column_already_has_changes_nothing()
    {
        var change = SchemaPatch.Build(Raw(Current), null, Raw("""{"Tickets":"url"}"""), null, null, null);

        Assert.Empty(change.Properties);
        Assert.Contains("already of type url", Assert.Single(change.Notes));
    }

    [Fact]
    public void Set_select_options_keeps_id_and_color_of_the_options_that_stay()
    {
        var change = SchemaPatch.Build(Raw(Current), null, null, Raw("""{"Status":["reviewed","Todo","Highlight"]}"""), null, null);

        var options = (JsonArray)change.Properties["Status"]!["select"]!["options"]!;
        Assert.Equal(
            """[{"id":"id-b","color":"green"},{"id":"id-a","color":"red"},{"name":"Highlight"}]""",
            options.ToJsonString());
        Assert.Contains(change.Notes, n => n.Contains("added: Highlight", StringComparison.Ordinal));
        Assert.Contains(change.Notes, n => n.Contains("removed", StringComparison.Ordinal) && n.Contains("Old", StringComparison.Ordinal));
        Assert.Contains(change.Notes, n => n.Contains("cannot rename", StringComparison.Ordinal));
    }

    [Fact]
    public void Set_select_options_works_for_a_multi_select_and_drops_duplicates()
    {
        var current = Raw("""{"Name":{"type":"title"},"Tags":{"type":"multi_select","multi_select":{"options":[{"id":"x","name":"a","color":"blue"}]}}}""");

        var change = SchemaPatch.Build(current, null, null, Raw("""{"Tags":["a","A","b"]}"""), null, null);

        Assert.Equal(
            """{"Tags":{"multi_select":{"options":[{"id":"x","color":"blue"},{"name":"b"}]}}}""",
            change.Properties.ToJsonString());
    }

    [Fact]
    public void Set_select_options_on_a_column_converted_in_the_same_call_starts_from_no_options()
    {
        var change = SchemaPatch.Build(Raw(Current), null, Raw("""{"Tickets":"select"}"""), Raw("""{"Tickets":["A","B"]}"""), null, null);

        Assert.Equal("""{"Tickets":{"select":{"options":[{"name":"A"},{"name":"B"}]}}}""", change.Properties.ToJsonString());
    }

    [Fact]
    public void Set_select_options_is_refused_on_a_column_that_is_not_a_select()
    {
        var e = Assert.Throws<ToolException>(() => SchemaPatch.Build(Raw(Current), null, null, Raw("""{"Tickets":["A"]}"""), null, null));

        Assert.Contains("not select", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_and_removing_columns()
    {
        var change = SchemaPatch.Build(Raw(Current), null, null, null, Raw("""{"Hours":"number","Done":"checkbox"}"""), new JsonArray("Tickets"));

        Assert.Equal("""{"Tickets":null,"Hours":{"number":{}},"Done":{"checkbox":{}}}""", change.Properties.ToJsonString());
    }

    [Theory]
    [InlineData("""{"Nope":"x"}""", "there is no column named \"Nope\"")]
    [InlineData("""{"Tickets":"Status"}""", "already used")]
    [InlineData("""{"Tickets":""}""", "non-empty string")]
    public void A_bad_rename_is_reported_before_anything_is_sent(string rename, string message)
    {
        var e = Assert.Throws<ToolException>(() => SchemaPatch.Build(Raw(Current), Raw(rename), null, null, null, null));

        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_title_column_cannot_be_removed_or_converted_but_can_be_renamed()
    {
        Assert.Throws<ToolException>(() => SchemaPatch.Build(Raw(Current), null, null, null, null, new JsonArray("Name")));
        Assert.Throws<ToolException>(() => SchemaPatch.Build(Raw(Current), null, Raw("""{"Name":"rich_text"}"""), null, null, null));
        Assert.Equal(
            """{"Name":{"name":"Deliverable"}}""",
            SchemaPatch.Build(Raw(Current), Raw("""{"Name":"Deliverable"}"""), null, null, null, null).Properties.ToJsonString());
    }

    [Fact]
    public void A_type_outside_the_supported_list_is_rejected()
    {
        var e = Assert.Throws<ToolException>(() => SchemaPatch.Build(Raw(Current), null, Raw("""{"Tickets":"formula"}"""), null, null, null));

        Assert.Contains("number, rich_text, select, multi_select, date, checkbox, url", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_column_cannot_be_removed_and_changed_in_one_call()
    {
        Assert.Throws<ToolException>(() => SchemaPatch.Build(Raw(Current), Raw("""{"Tickets":"X"}"""), null, null, null, new JsonArray("Tickets")));
    }

    // ------------------------------------------------------------------ create_database properties

    [Fact]
    public void New_database_properties_accept_type_names_and_select_options()
    {
        var properties = SchemaPatch.BuildNew(Raw("""{"Name":"title","Status":{"select":["Todo","Done"]},"Tags":{"multi_select":["a"]},"Hours":"number"}"""));

        Assert.Equal(
            """{"Name":{"title":{}},"Status":{"select":{"options":[{"name":"Todo"},{"name":"Done"}]}},"Tags":{"multi_select":{"options":[{"name":"a"}]}},"Hours":{"number":{}}}""",
            properties.ToJsonString());
    }

    [Theory]
    [InlineData("""{"Hours":"number"}""")]
    [InlineData("""{"A":"title","B":"title"}""")]
    [InlineData("""{}""")]
    public void A_new_database_needs_exactly_one_title_column(string json) =>
        Assert.Throws<ToolException>(() => SchemaPatch.BuildNew(Raw(json)));

    [Fact]
    public void A_new_database_rejects_an_unknown_type_and_options_on_other_types()
    {
        Assert.Throws<ToolException>(() => SchemaPatch.BuildNew(Raw("""{"Name":"title","X":"relation"}""")));
        Assert.Throws<ToolException>(() => SchemaPatch.BuildNew(Raw("""{"Name":"title","X":{"number":["a"]}}""")));
    }

    [Fact]
    public void Describe_shows_option_colors_and_the_database_icon()
    {
        var source = Raw("""{"id":"ds","title":[{"plain_text":"Deliveries"}],"parent":{"database_id":"db"},"properties":""" + Current + "}");

        var text = DataSourceText.Describe(source, Raw("""{"type":"emoji","emoji":"🚚"}""")).ReplaceLineEndings("\n");

        Assert.Contains("# Deliveries", text, StringComparison.Ordinal);
        Assert.Contains("icon: 🚚", text, StringComparison.Ordinal);
        Assert.Contains("- Status (select): Todo (red) | Reviewed (green) | Old (gray)", text, StringComparison.Ordinal);
    }
}
