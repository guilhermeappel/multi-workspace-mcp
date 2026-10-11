using NotionMultiMcp.Configuration;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Tests;

public sealed class WorkspaceRegistryTests
{
    /// <summary>Goes through the validator first, as at startup; a rejected configuration throws with every error line.</summary>
    private static WorkspaceRegistry Load(params (string Name, string? Token)[] workspaces)
    {
        var options = new NotionOptions { Workspaces = workspaces.ToDictionary(w => w.Name, w => w.Token) };
        var result = new NotionOptionsValidator().Validate(null, options);
        if (result.Failed)
        {
            throw new InvalidOperationException(result.FailureMessage);
        }

        return WorkspaceRegistry.From(options);
    }

    private static WorkspaceRegistry LoadReadOnly(string? readOnly, params (string Name, string? Token)[] workspaces)
    {
        var options = new NotionOptions { Workspaces = workspaces.ToDictionary(w => w.Name, w => w.Token), ReadOnlyWorkspaces = readOnly };
        var result = new NotionOptionsValidator().Validate(null, options);
        if (result.Failed)
        {
            throw new InvalidOperationException(result.FailureMessage);
        }

        return WorkspaceRegistry.From(options);
    }

    [Fact]
    public void Each_configured_entry_becomes_a_lowercase_workspace()
    {
        var registry = Load(("Work", "ntn_w"), ("PERSONAL", "ntn_p"));

        Assert.Equal(["personal", "work"], registry.Names);
    }

    [Fact]
    public void Underscores_in_the_name_become_hyphens()
    {
        var registry = Load(("my_client", "ntn_c"));

        Assert.Equal(["my-client"], registry.Names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_token_stops_the_startup_and_names_the_entry(string? token)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(("work", "ntn_w"), ("empty", token)));

        Assert.Contains("Notion:Workspaces:empty", error.Message);
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("a b")]
    [InlineData("-work")]
    [InlineData("__x")]
    [InlineData("")]
    public void A_name_that_is_not_valid_stops_the_startup(string name)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(("work", "ntn_w"), (name, "ntn_x")));

        Assert.Contains("not a valid workspace name", error.Message);
    }

    [Fact]
    public void Two_entries_that_normalise_to_the_same_name_stop_the_startup()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(("my_client", "ntn_a"), ("my-client", "ntn_b")));

        Assert.Contains("duplicates", error.Message);
    }

    [Fact]
    public void Having_no_workspace_is_a_startup_error_that_names_the_section()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load());

        Assert.Contains("Notion:Workspaces", error.Message);
    }

    [Fact]
    public void A_token_is_found_by_workspace_name_ignoring_case_and_spaces()
    {
        var registry = Load(("work", "  ntn_w  "), ("personal", "ntn_p"));

        Assert.Equal("ntn_w", registry.TokenFor("work"));
        Assert.Equal("ntn_w", registry.TokenFor("  WORK "));
        Assert.Equal("ntn_p", registry.TokenFor("personal"));
    }

    [Fact]
    public void With_a_single_workspace_the_name_is_optional()
    {
        var registry = Load(("work", "ntn_w"));

        Assert.Equal("ntn_w", registry.TokenFor(null));
        Assert.Equal("ntn_w", registry.TokenFor("  "));
    }

    [Fact]
    public void With_several_workspaces_the_name_is_required_and_the_options_are_listed()
    {
        var registry = Load(("work", "ntn_w"), ("personal", "ntn_p"));

        var error = Assert.Throws<ToolException>(() => registry.TokenFor(null));

        Assert.Contains("personal, work", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ,  ")]
    public void No_workspace_is_read_only_by_default(string? readOnly)
    {
        var registry = LoadReadOnly(readOnly, ("work", "ntn_w"), ("personal", "ntn_p"));

        Assert.False(registry.IsReadOnly("work"));
        Assert.False(registry.IsReadOnly("personal"));
    }

    [Fact]
    public void A_listed_workspace_is_read_only_and_the_others_are_not()
    {
        var registry = LoadReadOnly(" Work , client_x ", ("work", "ntn_w"), ("personal", "ntn_p"), ("client-x", "ntn_c"));

        Assert.True(registry.IsReadOnly("work"));
        Assert.True(registry.IsReadOnly("  WORK "));
        Assert.True(registry.IsReadOnly("client-x"));
        Assert.False(registry.IsReadOnly("personal"));
    }

    [Fact]
    public void With_a_single_workspace_an_omitted_name_means_that_workspace()
    {
        var registry = LoadReadOnly("work", ("work", "ntn_w"));

        Assert.True(registry.IsReadOnly(null));
        Assert.True(registry.IsReadOnly("  "));
    }

    [Fact]
    public void An_omitted_name_with_several_workspaces_is_not_read_only_and_fails_later_in_token_lookup()
    {
        var registry = LoadReadOnly("work", ("work", "ntn_w"), ("personal", "ntn_p"));

        Assert.False(registry.IsReadOnly(null));
        Assert.Throws<ToolException>(() => registry.TokenFor(null));
    }

    [Fact]
    public void An_unknown_name_is_not_read_only()
    {
        var registry = LoadReadOnly("work", ("work", "ntn_w"));

        Assert.False(registry.IsReadOnly("clients"));
    }

    [Theory]
    [InlineData("wrok")]
    [InlineData("work,persnal")]
    public void A_read_only_name_that_is_not_a_configured_workspace_stops_the_startup(string readOnly)
    {
        var error = Assert.Throws<InvalidOperationException>(() => LoadReadOnly(readOnly, ("work", "ntn_w"), ("personal", "ntn_p")));

        Assert.Contains("Notion:ReadOnlyWorkspaces", error.Message);
        Assert.Contains("not a configured workspace", error.Message);
    }

    [Fact]
    public void An_unknown_workspace_is_a_tool_error_that_lists_the_options()
    {
        var registry = Load(("work", "ntn_w"));

        var error = Assert.Throws<ToolException>(() => registry.TokenFor("clients"));

        Assert.Contains("Unknown workspace 'clients'", error.Message);
        Assert.Contains("work", error.Message);
    }
}
