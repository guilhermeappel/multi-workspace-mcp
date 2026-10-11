using System.Collections.Concurrent;

namespace NotionMultiMcp.Auth;

/// <summary>
/// Remembers redeemed authorization codes so each one can be used only once. Codes are stateless
/// and short-lived (5 minutes), so in-memory anti-replay is enough; it is per process, which is
/// why the app runs with a single replica.
/// </summary>
internal sealed class AuthorizationCodeLedger(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, long> _redeemed = new();

    /// <summary>Returns false when the code, identified by its nonce, was already redeemed.</summary>
    public bool TryRedeem(string nonce, long expiresAt)
    {
        if (!_redeemed.TryAdd(nonce, expiresAt))
        {
            return false;
        }

        RemoveExpired();
        return true;
    }

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        foreach (var (nonce, expiresAt) in _redeemed)
        {
            if (expiresAt < now)
            {
                _redeemed.TryRemove(nonce, out _);
            }
        }
    }
}
