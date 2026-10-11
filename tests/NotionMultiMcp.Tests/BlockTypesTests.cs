using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Tests;

public class BlockTypesTests
{
    [Theory]
    [InlineData(1, BlockTypes.Heading1)]
    [InlineData(2, BlockTypes.Heading2)]
    [InlineData(3, BlockTypes.Heading3)]
    public void Heading_maps_each_level_to_its_block_type(int level, string expected)
    {
        Assert.Equal(expected, BlockTypes.Heading(level));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Heading_throws_for_a_level_notion_does_not_have(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockTypes.Heading(level));
    }
}
