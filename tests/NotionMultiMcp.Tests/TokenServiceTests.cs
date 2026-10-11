using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using NotionMultiMcp.Auth;

namespace NotionMultiMcp.Tests;

public class TokenServiceTests
{
    private const string Password = "a-very-secure-password";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private static ServerSettings Settings(int generation = 1, byte[]? key = null) => new()
    {
        PublicBaseUrl = "https://mcp.example.com",
        AuthPassword = Password,
        SigningKey = key ?? RandomNumberGenerator.GetBytes(32),
        AuthDisabled = false,
        TokenGeneration = generation,
        ExtraRedirectUris = [],
        ExtraAllowedOrigins = [],
    };

    [Fact]
    public void Protect_and_unprotect_round_trip()
    {
        var service = new TokenService(Settings(), _time);
        var token = service.Protect(TokenPurpose.Client, new ClientRegistration(["https://claude.ai/api/mcp/auth_callback"], "Claude", 123));

        Assert.True(service.TryUnprotect<ClientRegistration>(TokenPurpose.Client, token, out var registration));
        Assert.Equal("Claude", registration.Name);
        Assert.Equal(123L, registration.IssuedAt);
    }

    // Tokens already issued must stay valid across releases: the signature is HMAC-SHA256 over "<purpose>|<body>" and
    // the body is the camelCase JSON of the payload. This pins both, independently of how TokenService is written.
    [Theory]
    [InlineData("Client", "client")]
    [InlineData("Code", "code")]
    [InlineData("Access", "access")]
    [InlineData("Refresh", "refresh")]
    public void Token_format_is_a_stable_contract(string purposeName, string wireName)
    {
        var purpose = Enum.Parse<TokenPurpose>(purposeName);
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var service = new TokenService(Settings(key: key), _time);

        var token = service.Protect(purpose, new AccessToken(1790000000, 7));

        var parts = token.Split('.');
        var body = parts[0];
        var expectedSignature = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(wireName + "|" + body)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(body.Replace('-', '+').Replace('_', '/').PadRight((body.Length + 3) / 4 * 4, '=')));
        Assert.Equal("""{"expiresAt":1790000000,"generation":7}""", json);
        Assert.Equal(expectedSignature, parts[1]);
    }

    [Fact]
    public void Protect_throws_for_a_purpose_that_is_not_defined()
    {
        var service = new TokenService(Settings(), _time);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.Protect((TokenPurpose)99, new AccessToken(1, 1)));
    }

    [Fact]
    public void Unprotect_rejects_other_purpose_tampering_and_other_key()
    {
        var service = new TokenService(Settings(), _time);
        var token = service.Protect(TokenPurpose.Code, new AccessToken(service.Now + 60, 1));

        Assert.False(service.TryUnprotect<AccessToken>(TokenPurpose.Access, token, out _));
        Assert.False(service.TryUnprotect<AccessToken>(TokenPurpose.Code, "x" + token, out _));
        Assert.False(new TokenService(Settings(), _time).TryUnprotect<AccessToken>(TokenPurpose.Code, token, out _));
        Assert.False(service.TryUnprotect<AccessToken>(TokenPurpose.Code, "no-dot", out _));
    }

    [Fact]
    public void Access_token_validation_checks_expiry_and_generation()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var service = new TokenService(Settings(generation: 2, key: key), _time);

        var valid = service.Protect(TokenPurpose.Access, new AccessToken(service.Now + 60, 2));
        var expired = service.Protect(TokenPurpose.Access, new AccessToken(service.Now - 1, 2));
        var oldGeneration = service.Protect(TokenPurpose.Access, new AccessToken(service.Now + 60, 1));

        Assert.True(service.IsValidAccessToken("Bearer " + valid));
        Assert.False(service.IsValidAccessToken("Bearer " + expired));
        Assert.False(service.IsValidAccessToken("Bearer " + oldGeneration));
        Assert.False(service.IsValidAccessToken(valid));
        Assert.False(service.IsValidAccessToken(null));
    }

    [Fact]
    public void Access_token_expires_as_the_clock_advances()
    {
        var service = new TokenService(Settings(), _time);
        var token = service.Protect(TokenPurpose.Access, new AccessToken(service.Now + 3600, 1));

        Assert.True(service.IsValidAccessToken("Bearer " + token));

        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.True(service.IsValidAccessToken("Bearer " + token));

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(service.IsValidAccessToken("Bearer " + token));
    }

    [Fact]
    public void Now_is_unix_seconds_from_the_time_provider()
    {
        var service = new TokenService(Settings(), _time);

        Assert.Equal(_time.GetUtcNow().ToUnixTimeSeconds(), service.Now);
    }

    [Fact]
    public void Password_comparison()
    {
        var service = new TokenService(Settings(), _time);

        Assert.True(service.PasswordMatches(Password));
        Assert.False(service.PasswordMatches("wrong-password"));
        Assert.False(service.PasswordMatches(null));
    }
}
