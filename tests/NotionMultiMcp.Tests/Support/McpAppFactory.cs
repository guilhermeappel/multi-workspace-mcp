using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Tests.Support;

/// <summary>
/// Hosts the real application in memory with a fake clock and a fake Notion API. Settings are passed
/// with UseSetting, which puts them in the host configuration the application is built from.
/// </summary>
internal sealed class McpAppFactory : WebApplicationFactory<Program>
{
    public const string Password = "a-test-password-123";
    public const string WorkToken = "ntn_work_token";
    public const string PersonalToken = "ntn_personal_token";
    public const string PublicBaseUrl = "https://mcp.test";

    static McpAppFactory()
    {
        // Keep the tests hermetic: settings from the developer's machine must not leak into the host.
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string key
                && (key.StartsWith("Server__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Notion__", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }
    }

    private readonly IReadOnlyDictionary<string, string?> _extraSettings;

    public McpAppFactory(IReadOnlyDictionary<string, string?>? extraSettings = null)
    {
        _extraSettings = extraSettings ?? new Dictionary<string, string?>();
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Everything the application logged, for asserting on warnings (and on what must never be logged).</summary>
    public LogCollector Logs { get; } = new();

    /// <summary>The fake Notion API. Replace its responders per test with <see cref="StubHttpHandler.Respond"/>.</summary>
    public StubHttpHandler Notion { get; } = new(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
    {
        Content = new StringContent("""{"object":"error","code":"object_not_found","message":"No response configured."}"""),
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" (not Development) so user-secrets are not loaded and Server:AuthDisabled cannot apply.
        builder.UseEnvironment("Testing");
        builder.UseSetting("Server:PublicBaseUrl", PublicBaseUrl);
        builder.UseSetting("Server:AuthPassword", Password);
        builder.UseSetting("Server:SigningKey", Convert.ToHexString([.. new byte[32].Select((_, i) => (byte)(i + 1))]));
        builder.UseSetting("Notion:Workspaces:work", WorkToken);
        builder.UseSetting("Notion:Workspaces:personal", PersonalToken);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        foreach (var (name, value) in _extraSettings)
        {
            builder.UseSetting(name, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.AddHttpClient<NotionClient>().ConfigurePrimaryHttpMessageHandler(() => Notion);
        });
    }

    public HttpClient CreateNoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}
