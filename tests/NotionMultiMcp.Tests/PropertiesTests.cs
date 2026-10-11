using System.Text.Json.Nodes;
using NotionMultiMcp;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Tests;

public class PropertiesTests
{
    private static readonly Dictionary<string, string> _schema = new()
    {
        ["Name"] = "title",
        ["Status"] = "status",
        ["Tags"] = "multi_select",
        ["Due"] = "date",
        ["Done"] = "checkbox",
        ["Points"] = "number",
        ["Owner"] = "people",
    };

    [Fact]
    public void Build_converts_simple_values_using_schema()
    {
        var input = JsonNode.Parse("""
            {"Status":"Done","Tags":["a","b"],"Due":"2026-10-01","Done":true,"Points":"3.5"}
            """)!.AsObject();

        var result = Properties.Build(_schema, input);

        Assert.Equal("Done", result["Status"]!["status"]!["name"]!.GetValue<string>());
        Assert.Equal(2, result["Tags"]!["multi_select"]!.AsArray().Count);
        Assert.Equal("2026-10-01", result["Due"]!["date"]!["start"]!.GetValue<string>());
        Assert.True(result["Done"]!["checkbox"]!.GetValue<bool>());
        Assert.Equal(3.5, result["Points"]!["number"]!.GetValue<double>());
    }

    [Fact]
    public void Build_accepts_comma_separated_multi_select()
    {
        var result = Properties.Build(_schema, JsonNode.Parse("""{"Tags":"x, y ,z"}""")!.AsObject());
        var names = result["Tags"]!["multi_select"]!.AsArray().Select(t => t!["name"]!.GetValue<string>());
        Assert.Equal(new[] { "x", "y", "z" }, names);
    }

    [Fact]
    public void Build_passes_raw_notion_format_through()
    {
        var result = Properties.Build(_schema, JsonNode.Parse("""{"Status":{"status":{"name":"Doing"}}}""")!.AsObject());
        Assert.Equal("Doing", result["Status"]!["status"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Build_rejects_unknown_properties()
    {
        var ex = Assert.Throws<ToolException>(() =>
            Properties.Build(_schema, JsonNode.Parse("""{"Nope":"x"}""")!.AsObject()));
        Assert.Contains("Status (status)", ex.Message);
    }

    [Fact]
    public void Format_reads_common_types()
    {
        var page = JsonNode.Parse("""
            {"properties":{
              "Name":{"type":"title","title":[{"plain_text":"Tarefa"}]},
              "Tags":{"type":"multi_select","multi_select":[{"name":"a"},{"name":"b"}]},
              "Due":{"type":"date","date":{"start":"2026-10-01","end":"2026-10-03"}}
            }}
            """);

        Assert.Equal("Tarefa", Properties.PageTitle(page));
        Assert.Equal("a, b", Properties.Format(page!["properties"]!["Tags"]));
        Assert.Equal("2026-10-01 → 2026-10-03", Properties.Format(page["properties"]!["Due"]));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("3,5")]
    [InlineData("three")]
    public void Build_rejects_text_that_is_not_a_number(string value)
    {
        var input = new JsonObject { ["Points"] = value };

        var ex = Assert.Throws<ToolException>(() => Properties.Build(_schema, input));

        Assert.Contains("Points", ex.Message);
        Assert.Contains("number", ex.Message);
    }

    [Fact]
    public void Build_rejects_an_object_where_a_number_is_expected()
    {
        var input = JsonNode.Parse("""{"Points":{"value":3}}""")!.AsObject();

        Assert.Throws<ToolException>(() => Properties.Build(_schema, input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Build_clears_a_number_with_null_or_blank_text(string? value)
    {
        var input = new JsonObject { ["Points"] = value };

        var result = Properties.Build(_schema, input);

        Assert.Null(result["Points"]!["number"]);
    }

    [Fact]
    public void Build_accepts_a_json_number()
    {
        var result = Properties.Build(_schema, JsonNode.Parse("""{"Points":5}""")!.AsObject());

        Assert.Equal(5.0, result["Points"]!["number"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("TRUE", true)]
    public void Build_accepts_a_checkbox_given_as_text(string value, bool expected)
    {
        var result = Properties.Build(_schema, new JsonObject { ["Done"] = value });

        Assert.Equal(expected, result["Done"]!["checkbox"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("yes")]
    [InlineData("1")]
    public void Build_rejects_a_checkbox_value_that_is_not_a_boolean(string value)
    {
        var ex = Assert.Throws<ToolException>(() => Properties.Build(_schema, new JsonObject { ["Done"] = value }));

        Assert.Contains("Done", ex.Message);
        Assert.Contains("true or false", ex.Message);
    }

    // ------------------------------------------------------------------ reading every property type

    [Theory]
    [InlineData("""{"type":"title","title":[{"plain_text":"Hi"}]}""", "Hi")]
    [InlineData("""{"type":"rich_text","rich_text":[{"plain_text":"a"},{"plain_text":"b"}]}""", "ab")]
    [InlineData("""{"type":"number","number":3.5}""", "3.5")]
    [InlineData("""{"type":"number","number":null}""", "")]
    [InlineData("""{"type":"select","select":{"name":"A"}}""", "A")]
    [InlineData("""{"type":"select","select":null}""", "")]
    [InlineData("""{"type":"status","status":{"name":"Doing"}}""", "Doing")]
    [InlineData("""{"type":"multi_select","multi_select":[{"name":"x"},{"name":"y"}]}""", "x, y")]
    [InlineData("""{"type":"date","date":{"start":"2026-10-01"}}""", "2026-10-01")]
    [InlineData("""{"type":"date","date":{"start":"2026-10-01","end":"2026-10-03"}}""", "2026-10-01 → 2026-10-03")]
    [InlineData("""{"type":"date","date":null}""", "")]
    [InlineData("""{"type":"checkbox","checkbox":true}""", "yes")]
    [InlineData("""{"type":"checkbox","checkbox":false}""", "no")]
    [InlineData("""{"type":"url","url":"https://example.com"}""", "https://example.com")]
    [InlineData("""{"type":"email","email":"a@b.dev"}""", "a@b.dev")]
    [InlineData("""{"type":"phone_number","phone_number":"+55 11 99999-0000"}""", "+55 11 99999-0000")]
    [InlineData("""{"type":"people","people":[{"name":"Ana"},{"id":"user-2"}]}""", "Ana, user-2")]
    [InlineData("""{"type":"created_time","created_time":"2026-09-30T10:00:00Z"}""", "2026-09-30T10:00:00Z")]
    [InlineData("""{"type":"last_edited_time","last_edited_time":"2026-09-30T11:00:00Z"}""", "2026-09-30T11:00:00Z")]
    [InlineData("""{"type":"created_by","created_by":{"name":"Ana","id":"u1"}}""", "Ana")]
    [InlineData("""{"type":"last_edited_by","last_edited_by":{"id":"u2"}}""", "u2")]
    [InlineData("""{"type":"files","files":[{"name":"a.pdf"},{"name":"b.png"}]}""", "a.pdf, b.png")]
    [InlineData("""{"type":"unique_id","unique_id":{"prefix":"TASK","number":42}}""", "TASK-42")]
    [InlineData("""{"type":"unique_id","unique_id":{"prefix":null,"number":7}}""", "7")]
    [InlineData("""{"type":"verification","verification":{"state":"verified"}}""", "verified")]
    public void Format_reads_each_property_type(string propertyJson, string expected)
    {
        Assert.Equal(expected, Properties.Format(JsonNode.Parse(propertyJson)));
    }

    [Theory]
    [InlineData("""{"type":"formula","formula":{"type":"string","string":"calc"}}""", "calc")]
    [InlineData("""{"type":"formula","formula":{"type":"boolean","boolean":true}}""", "yes")]
    [InlineData("""{"type":"formula","formula":{"type":"number","number":12}}""", "12")]
    [InlineData("""{"type":"formula","formula":{"type":"date","date":{"start":"2026-10-01"}}}""", "2026-10-01")]
    [InlineData("""{"type":"rollup","rollup":{"type":"number","number":9}}""", "9")]
    [InlineData("""{"type":"rollup","rollup":{"type":"date","date":{"start":"2026-10-02"}}}""", "2026-10-02")]
    [InlineData("""{"type":"rollup","rollup":{"type":"array","array":[{"type":"number","number":1},{"type":"number","number":2}]}}""", "1, 2")]
    public void Format_reads_formulas_and_rollups(string propertyJson, string expected)
    {
        Assert.Equal(expected, Properties.Format(JsonNode.Parse(propertyJson)));
    }

    [Fact]
    public void Format_shortens_long_relation_lists()
    {
        var ids = string.Join(",", Enumerable.Range(1, 12).Select(i => $$"""{"id":"r{{i}}"}"""));
        var property = JsonNode.Parse($$"""{"type":"relation","relation":[{{ids}}]}""");

        var text = Properties.Format(property);

        Assert.StartsWith("r1, r2, r3", text);
        Assert.EndsWith("r10 … (+2)", text);
    }

    [Fact]
    public void Format_lists_short_relations_in_full_and_ignores_missing_types()
    {
        Assert.Equal("a, b", Properties.Format(JsonNode.Parse("""{"type":"relation","relation":[{"id":"a"},{"id":"b"}]}""")));
        Assert.Equal("", Properties.Format(JsonNode.Parse("""{"no":"type"}""")));
        Assert.Equal("", Properties.Format(null));
    }

    [Fact]
    public void Page_title_falls_back_to_untitled()
    {
        Assert.Equal("(untitled)", Properties.PageTitle(JsonNode.Parse("""{"properties":{"Name":{"type":"title","title":[]}}}""")));
        Assert.Equal("(untitled)", Properties.PageTitle(JsonNode.Parse("""{"properties":{}}""")));
        Assert.Equal("Hello", Properties.PageTitle(JsonNode.Parse("""{"properties":{"Other":{"type":"rich_text"},"Name":{"type":"title","title":[{"plain_text":"Hello"}]}}}""")));
    }

    [Fact]
    public void The_title_property_is_found_by_type_and_its_absence_is_an_error()
    {
        Assert.Equal("Name", Properties.TitlePropertyName(Properties.SchemaTypes(JsonNode.Parse("""{"Status":{"type":"status"},"Name":{"type":"title"}}"""))));
        Assert.Throws<ToolException>(() => Properties.TitlePropertyName(new Dictionary<string, string> { ["Status"] = "status" }));
    }

    // ------------------------------------------------------------------ writing the remaining types

    private static readonly Dictionary<string, string> _writableSchema = new()
    {
        ["Note"] = "rich_text",
        ["Priority"] = "select",
        ["Site"] = "url",
        ["Mail"] = "email",
        ["Phone"] = "phone_number",
        ["Owner"] = "people",
        ["Project"] = "relation",
        ["Due"] = "date",
        ["Total"] = "formula",
    };

    [Fact]
    public void Build_converts_text_select_url_email_and_phone()
    {
        var result = Properties.Build(_writableSchema, JsonNode.Parse("""{"Note":"hello","Priority":"High","Site":"https://x.dev","Mail":"a@b.dev","Phone":"123"}""")!.AsObject());

        Assert.Equal("hello", (string)result["Note"]!["rich_text"]![0]!["text"]!["content"]!);
        Assert.Equal("High", (string)result["Priority"]!["select"]!["name"]!);
        Assert.Equal("https://x.dev", (string)result["Site"]!["url"]!);
        Assert.Equal("a@b.dev", (string)result["Mail"]!["email"]!);
        Assert.Equal("123", (string)result["Phone"]!["phone_number"]!);
    }

    [Fact]
    public void Build_clears_a_select_url_or_date_given_an_empty_value()
    {
        var result = Properties.Build(_writableSchema, JsonNode.Parse("""{"Priority":"","Site":"","Due":""}""")!.AsObject());

        Assert.Null(result["Priority"]!["select"]);
        Assert.Null(result["Site"]!["url"]);
        Assert.Null(result["Due"]!["date"]);
    }

    [Fact]
    public void Build_accepts_a_date_range_object()
    {
        var result = Properties.Build(_writableSchema, JsonNode.Parse("""{"Due":{"start":"2026-10-01","end":"2026-10-05"}}""")!.AsObject());

        Assert.Equal("2026-10-05", (string)result["Due"]!["date"]!["end"]!);
    }

    [Fact]
    public void Build_converts_people_and_relations_to_id_lists()
    {
        var result = Properties.Build(_writableSchema, JsonNode.Parse("""{"Owner":["user-1","user-2"],"Project":["https://www.notion.so/acme/Plan-1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f"]}""")!.AsObject());

        Assert.Equal(["user-1", "user-2"], result["Owner"]!["people"]!.AsArray().Select(p => (string)p!["id"]!));
        Assert.Equal("user", (string)result["Owner"]!["people"]![0]!["object"]!);
        Assert.Equal("1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f", (string)result["Project"]!["relation"]![0]!["id"]!);
    }

    [Fact]
    public void Build_explains_how_to_set_a_type_that_has_no_simple_form()
    {
        var error = Assert.Throws<ToolException>(() => Properties.Build(_writableSchema, JsonNode.Parse("""{"Total":"5"}""")!.AsObject()));

        Assert.Contains("Property 'Total' has type 'formula'", error.Message);
        Assert.Contains("raw Notion format", error.Message);
    }

    [Fact]
    public void Build_returns_an_empty_object_when_there_is_no_input()
    {
        Assert.Empty(Properties.Build(_writableSchema, null));
    }

    [Theory]
    [InlineData("""{"type":"button","button":{}}""", "[unsupported property type: button]")]
    [InlineData("""{"type":"place","place":{"lat":1}}""", "[unsupported property type: place]")]
    public void Format_marks_a_property_type_it_does_not_know_instead_of_hiding_it(string propertyJson, string expected)
    {
        Assert.Equal(expected, Properties.Format(JsonNode.Parse(propertyJson)));
    }
}
