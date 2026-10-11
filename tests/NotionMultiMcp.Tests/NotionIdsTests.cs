using NotionMultiMcp;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Tests;

public class NotionIdsTests
{
    [Theory]
    [InlineData("1a2b3c4d-1a2b-1a2b-1a2b-1a2b3c4d5e6f", "1a2b3c4d-1a2b-1a2b-1a2b-1a2b3c4d5e6f")]
    [InlineData("1A2B3C4D1A2B1A2B1A2B1A2B3C4D5E6F", "1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f")]
    [InlineData("https://www.notion.so/acme/Onboarding-1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f?pvs=4", "1a2b3c4d1a2b1a2b1a2b1a2b3c4d5e6f")]
    [InlineData("https://www.notion.so/acme/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?v=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb&p=cccccccccccccccccccccccccccccccc&pm=s", "cccccccccccccccccccccccccccccccc")]
    [InlineData("https://www.notion.so/acme/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?v=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Parse_extracts_id(string input, string expected)
    {
        Assert.Equal(expected, NotionIds.Parse(input));
    }

    [Fact]
    public void Parse_throws_tool_exception_when_no_id()
    {
        Assert.Throws<ToolException>(() => NotionIds.Parse("https://www.notion.so/acme/no-id"));
    }
}
