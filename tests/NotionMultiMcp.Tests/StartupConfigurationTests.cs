using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotionMultiMcp.Configuration;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Tests;

public sealed class StartupConfigurationTests
{
    private static WebApplication Build(IDictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddServerConfiguration();
        return builder.Build();
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        ["Server:PublicBaseUrl"] = "https://mcp.example.com",
        ["Server:AuthPassword"] = "a-long-test-password",
        ["Server:SigningKey"] = Convert.ToHexString([.. Enumerable.Range(1, 32).Select(i => (byte)i)]),
        ["Notion:Workspaces:work"] = "ntn_work",
    };

    [Fact]
    public void A_valid_configuration_passes_without_any_message()
    {
        using var app = Build(Valid());
        using var errors = new StringWriter();

        var valid = StartupConfiguration.TryValidate(app.Services, errors);

        Assert.True(valid);
        Assert.Empty(errors.ToString());
        Assert.Equal("https://mcp.example.com", app.Services.GetRequiredService<ServerSettings>().PublicBaseUrl);
        Assert.Equal(["work"], app.Services.GetRequiredService<WorkspaceRegistry>().Names);
    }

    [Theory]
    [InlineData("Server:TokenGeneration", "two", "Server:TokenGeneration")]
    [InlineData("Server:AuthPassword", "short", "Server:AuthPassword")]
    [InlineData("Server:PublicBaseUrl", "http://example.com", "Server:PublicBaseUrl")]
    [InlineData("Server:SigningKey", "abcd", "Server:SigningKey")]
    public void A_settings_mistake_is_reported_as_one_clear_line_and_the_startup_is_refused(string name, string value, string expected)
    {
        var values = Valid();
        values[name] = value;
        using var app = Build(values);
        using var errors = new StringWriter();

        var valid = StartupConfiguration.TryValidate(app.Services, errors);

        Assert.False(valid);
        var message = errors.ToString().TrimEnd();
        Assert.StartsWith("Configuration error: ", message);
        Assert.Contains(expected, message);
        Assert.DoesNotContain('\n', message);
    }

    [Fact]
    public void Having_no_workspace_is_reported_too()
    {
        var values = Valid();
        values.Remove("Notion:Workspaces:work");
        using var app = Build(values);
        using var errors = new StringWriter();

        var valid = StartupConfiguration.TryValidate(app.Services, errors);

        Assert.False(valid);
        Assert.Contains("Notion:Workspaces", errors.ToString());
    }

    [Fact]
    public void Server_and_workspace_mistakes_are_reported_together()
    {
        var values = Valid();
        values["Server:AuthPassword"] = "short";
        values["Notion:Workspaces:work"] = " ";
        using var app = Build(values);
        using var errors = new StringWriter();

        var valid = StartupConfiguration.TryValidate(app.Services, errors);

        Assert.False(valid);
        var text = errors.ToString();
        Assert.Contains("Server:AuthPassword", text);
        Assert.Contains("Notion:Workspaces:work", text);
    }
}
