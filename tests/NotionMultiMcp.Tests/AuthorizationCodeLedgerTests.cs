using Microsoft.Extensions.Time.Testing;
using NotionMultiMcp.Auth;

namespace NotionMultiMcp.Tests;

public class AuthorizationCodeLedgerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private long InSeconds(int seconds) => _time.GetUtcNow().AddSeconds(seconds).ToUnixTimeSeconds();

    [Fact]
    public void A_code_can_be_redeemed_only_once()
    {
        var ledger = new AuthorizationCodeLedger(_time);
        var expiresAt = InSeconds(300);

        Assert.True(ledger.TryRedeem("nonce-1", expiresAt));
        Assert.False(ledger.TryRedeem("nonce-1", expiresAt));
    }

    [Fact]
    public void Different_codes_are_independent()
    {
        var ledger = new AuthorizationCodeLedger(_time);
        var expiresAt = InSeconds(300);

        Assert.True(ledger.TryRedeem("nonce-1", expiresAt));
        Assert.True(ledger.TryRedeem("nonce-2", expiresAt));
    }

    [Fact]
    public void Expired_entries_are_purged_when_another_code_is_redeemed()
    {
        var ledger = new AuthorizationCodeLedger(_time);
        Assert.True(ledger.TryRedeem("old", InSeconds(300)));

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(ledger.TryRedeem("new", InSeconds(300)));

        // "old" was purged, so the ledger no longer remembers it. That is safe only because an expired
        // code is rejected by the token endpoint before it reaches the ledger.
        Assert.True(ledger.TryRedeem("old", InSeconds(300)));
    }
}
