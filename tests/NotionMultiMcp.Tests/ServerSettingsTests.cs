using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using NotionMultiMcp.Configuration;

namespace NotionMultiMcp.Tests;

public sealed class ServerSettingsTests
{
    private const string Password = "a-long-test-password";

    private static readonly string _hexKey = Convert.ToHexString([.. Enumerable.Range(1, 32).Select(i => (byte)i)]);

    /// <summary>Goes through the real registration and startup validation; a rejected configuration throws with every error line.</summary>
    private static ServerSettings Load(IDictionary<string, string?> values, string environment = "Production")
    {
        // These tests are about the server section, so a workspace is always there.
        var withWorkspace = new Dictionary<string, string?>(values) { ["Notion:Workspaces:work"] = "ntn_work" };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(withWorkspace).Build());
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment });
        services.AddServerConfiguration();
        using var provider = services.BuildServiceProvider();

        using var errors = new StringWriter();
        if (!StartupConfiguration.TryValidate(provider, errors))
        {
            throw new InvalidOperationException(errors.ToString());
        }

        return provider.GetRequiredService<ServerSettings>();
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        ["Server:PublicBaseUrl"] = "https://mcp.example.com",
        ["Server:AuthPassword"] = Password,
        ["Server:SigningKey"] = _hexKey,
    };

    // ------------------------------------------------------------------ base url

    [Fact]
    public void The_base_url_defaults_to_localhost_and_the_mcp_url_is_derived_from_it()
    {
        var values = Valid();
        values.Remove("Server:PublicBaseUrl");

        var settings = Load(values);

        Assert.Equal("http://localhost:8080", settings.PublicBaseUrl);
        Assert.Equal("http://localhost:8080/mcp", settings.McpUrl);
    }

    [Fact]
    public void The_base_url_is_trimmed_and_loses_its_trailing_slash()
    {
        var values = Valid();
        values["Server:PublicBaseUrl"] = "  https://mcp.example.com//  ";

        var settings = Load(values);

        Assert.Equal("https://mcp.example.com", settings.PublicBaseUrl);
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:9000")]
    [InlineData("https://mcp.example.com")]
    public void Https_and_loopback_base_urls_are_accepted(string url)
    {
        var values = Valid();
        values["Server:PublicBaseUrl"] = url;

        Assert.Equal(url, Load(values).PublicBaseUrl);
    }

    [Theory]
    [InlineData("http://mcp.example.com")]
    [InlineData("ftp://mcp.example.com")]
    [InlineData("not a url")]
    [InlineData("/relative")]
    [InlineData("file:///etc/passwd")]
    public void Other_base_urls_are_rejected(string url)
    {
        var values = Valid();
        values["Server:PublicBaseUrl"] = url;

        var error = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains("Server:PublicBaseUrl", error.Message);
    }

    // ------------------------------------------------------------------ signing key

    [Fact]
    public void A_hex_key_of_32_bytes_is_accepted()
    {
        var settings = Load(Valid());

        Assert.Equal(32, settings.SigningKey.Length);
        Assert.Equal(Enumerable.Range(1, 32).Select(i => (byte)i), settings.SigningKey);
    }

    [Fact]
    public void A_base64_key_of_32_bytes_is_accepted()
    {
        var values = Valid();
        values["Server:SigningKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

        Assert.Equal(32, Load(values).SigningKey.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-key!")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 16 bytes as hex
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 32 identical bytes
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // a repeating pattern
    public void A_missing_or_short_key_is_rejected(string key)
    {
        var values = Valid();
        values["Server:SigningKey"] = key;

        var error = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains("Server:SigningKey", error.Message);
    }

    [Fact]
    public void A_passphrase_that_happens_to_be_valid_base64_is_rejected()
    {
        // 44 letters: valid base64 that decodes to 33 bytes, but with the guessing cost of the phrase.
        var values = Valid();
        values["Server:SigningKey"] = string.Concat(Enumerable.Repeat("correcthorsebatterystaple", 2))[..44];

        var error = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains("Server:SigningKey", error.Message);
        Assert.Contains("a phrase", error.Message);
    }

    [Theory]
    [InlineData("Z4z2j693FgTkRset1+klSnTieMaTRhDrsPIqzRj0F1I=")]
    [InlineData("38d9T2Gz6CfcJIHGo0xy7rDn+LqYxGKa6iKzDEUHlII=")]
    [InlineData("t3YGP62nMbRNz2WYnI2+uJFG9wEVS4s//CLCNbY5rIQ=")]
    public void Random_base64_keys_are_accepted(string key)
    {
        var values = Valid();
        values["Server:SigningKey"] = key;

        Assert.Equal(32, Load(values).SigningKey.Length);
    }

    // ------------------------------------------------------------------ password

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short-pass")]
    public void A_missing_or_short_password_is_rejected(string? password)
    {
        var values = Valid();
        values["Server:AuthPassword"] = password;

        var error = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains("Server:AuthPassword", error.Message);
    }

    [Fact]
    public void The_password_is_kept_as_given()
    {
        Assert.Equal(Password, Load(Valid()).AuthPassword);
    }

    // ------------------------------------------------------------------ Server:AuthDisabled

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void Auth_can_be_disabled_in_development_without_a_password_or_key(string value)
    {
        var settings = Load(new Dictionary<string, string?> { ["Server:AuthDisabled"] = value }, "Development");

        Assert.True(settings.AuthDisabled);
        Assert.Equal(32, settings.SigningKey.Length);
    }

    [Fact]
    public void Disabling_auth_generates_a_different_throwaway_key_each_time()
    {
        var values = new Dictionary<string, string?> { ["Server:AuthDisabled"] = "true" };

        Assert.NotEqual(Load(values, "Development").SigningKey, Load(values, "Development").SigningKey);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Auth_cannot_be_disabled_outside_development(string environment)
    {
        var values = new Dictionary<string, string?> { ["Server:AuthDisabled"] = "true" };

        // The flag is ignored, so the missing password and key are still errors.
        Assert.Throws<InvalidOperationException>(() => Load(values, environment));
    }

    [Fact]
    public void Outside_development_the_flag_does_not_change_a_valid_setup()
    {
        var values = Valid();
        values["Server:AuthDisabled"] = "true";

        Assert.False(Load(values).AuthDisabled);
    }

    [Fact]
    public void Auth_stays_enabled_when_the_flag_is_false()
    {
        var values = Valid();
        values["Server:AuthDisabled"] = "false";

        Assert.False(Load(values, "Development").AuthDisabled);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    public void A_flag_that_is_not_a_boolean_stops_the_startup(string value)
    {
        var values = Valid();
        values["Server:AuthDisabled"] = value;

        var error = Assert.Throws<InvalidOperationException>(() => Load(values, "Development"));

        Assert.Contains("Server:AuthDisabled", error.Message);
    }

    // ------------------------------------------------------------------ the rest

    [Fact]
    public void The_token_generation_defaults_to_one_and_can_be_set()
    {
        var values = Valid();
        Assert.Equal(1, Load(values).TokenGeneration);

        values["Server:TokenGeneration"] = "5";
        Assert.Equal(5, Load(values).TokenGeneration);
    }

    [Fact]
    public void Extra_redirect_uris_are_trimmed_and_blank_entries_dropped()
    {
        var values = Valid();
        values["Server:ExtraRedirectUris:0"] = " https://a.example/cb ";
        values["Server:ExtraRedirectUris:1"] = " ";
        values["Server:ExtraRedirectUris:2"] = "https://b.example/cb";

        Assert.Equal(["https://a.example/cb", "https://b.example/cb"], Load(values).ExtraRedirectUris);
    }

    [Fact]
    public void There_are_no_extra_redirect_uris_by_default()
    {
        Assert.Empty(Load(Valid()).ExtraRedirectUris);
    }

    [Fact]
    public void The_token_lifetimes_have_sensible_defaults()
    {
        var settings = Load(Valid());

        Assert.Equal(TimeSpan.FromHours(1), settings.AccessTokenLifetime);
        Assert.Equal(TimeSpan.FromDays(90), settings.RefreshTokenLifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.AuthorizationCodeLifetime);
    }

    [Fact]
    public void The_token_lifetimes_can_be_changed()
    {
        var values = Valid();
        values["Server:AccessTokenLifetime"] = "00:30:00";
        values["Server:RefreshTokenLifetime"] = "30.00:00:00";
        values["Server:AuthorizationCodeLifetime"] = "00:01:00";
        values["Server:MaxSessionLifetime"] = "60.00:00:00";

        var settings = Load(values);

        Assert.Equal(TimeSpan.FromMinutes(30), settings.AccessTokenLifetime);
        Assert.Equal(TimeSpan.FromDays(30), settings.RefreshTokenLifetime);
        Assert.Equal(TimeSpan.FromMinutes(1), settings.AuthorizationCodeLifetime);
        Assert.Equal(TimeSpan.FromDays(60), settings.MaxSessionLifetime);
    }

    [Theory]
    [InlineData("AccessTokenLifetime", "00:00:00")]
    [InlineData("RefreshTokenLifetime", "-1.00:00:00")]
    [InlineData("AuthorizationCodeLifetime", "00:00:00")]
    [InlineData("MaxSessionLifetime", "-00:00:01")]
    public void A_lifetime_that_is_not_positive_stops_the_startup(string name, string value)
    {
        var values = Valid();
        values[$"Server:{name}"] = value;

        var error = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains($"Server:{name}", error.Message);
    }

    [Fact]
    public void Every_mistake_is_reported_not_only_the_first()
    {
        var values = Valid();
        values["Server:PublicBaseUrl"] = "http://example.com";
        values["Server:AuthPassword"] = "short";
        values["Server:SigningKey"] = "abcd";

        var error = Assert.Throws<InvalidOperationException>(() => Load(values));

        var lines = error.Message.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("Configuration error: Server:", line));
    }

    [Fact]
    public void Extra_allowed_origins_are_trimmed_and_blank_entries_dropped()
    {
        var values = Valid();
        values["Server:ExtraAllowedOrigins:0"] = " https://inspector.example ";
        values["Server:ExtraAllowedOrigins:1"] = "";
        values["Server:ExtraAllowedOrigins:2"] = "http://localhost:6274";

        Assert.Equal(["https://inspector.example", "http://localhost:6274"], Load(values).ExtraAllowedOrigins);
    }

    [Fact]
    public void There_are_no_extra_allowed_origins_by_default()
    {
        Assert.Empty(Load(Valid()).ExtraAllowedOrigins);
    }

    [Theory]
    [InlineData("123456789012345")]
    [InlineData("a-bit-too-short")]
    public void A_password_of_fifteen_characters_is_rejected(string password)
    {
        var values = Valid();
        values["Server:AuthPassword"] = password;

        Assert.Equal(15, password.Length);
        Assert.Throws<InvalidOperationException>(() => Load(values));
    }

    [Fact]
    public void A_password_of_sixteen_characters_is_accepted()
    {
        var values = Valid();
        values["Server:AuthPassword"] = "1234567890123456";

        Assert.Equal("1234567890123456", Load(values).AuthPassword);
    }

    [Theory]
    [InlineData("two")]
    [InlineData("2.5")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("99999999999")]
    [InlineData("2,000")]
    public void A_token_generation_that_is_not_a_positive_whole_number_stops_the_startup(string value)
    {
        var values = Valid();
        values["Server:TokenGeneration"] = value;

        var error = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains("Server:TokenGeneration", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_token_generation_stops_the_startup_instead_of_silently_using_the_default(string value)
    {
        var values = Valid();
        values["Server:TokenGeneration"] = value;

        var error = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains("Server:TokenGeneration", error.Message);
    }

    [Fact]
    public void A_session_lasts_at_most_a_hundred_and_eighty_days()
    {
        Assert.Equal(TimeSpan.FromDays(180), Load(Valid()).MaxSessionLifetime);
    }
}
