using System.Net;
using System.Net.Sockets;

namespace NotionMultiMcp.Auth;

/// <summary>
/// Limits password guesses. Each client gets <see cref="MaxAttemptsPerClient"/> attempts per <see cref="Window"/>,
/// and the server as a whole gets <see cref="MaxAttemptsOverall"/>. A client is an IPv4 address or an IPv6 /64
/// network (see <see cref="ClientKeyFor"/>).
/// </summary>
/// <remarks>
/// An attempt is counted when it starts, under a lock, and refunded when the password turns out to be right, so
/// a burst of concurrent requests cannot all pass the check before any failure is recorded. The state is in
/// memory and per process (see ADR 0003): it resets when the app scales to zero, so it slows guessing down but
/// does not replace a long, random password.
///
/// The server-wide limit is what slows down an attacker who spreads guesses over many addresses, and it has a cost:
/// an attacker who uses enough addresses can use up the whole budget and keep the owner from starting a new login
/// until the attempts expire. Sessions that already exist are not affected, because refresh tokens do not go through
/// the throttle. That trade-off is accepted (ADR 0003).
/// </remarks>
internal sealed class LoginThrottle(TimeProvider timeProvider)
{
    public const string UnknownClient = "unknown";
    public const int MaxAttemptsPerClient = 10;
    public const int MaxAttemptsOverall = 60;

    private const int IPv6AddressBytes = 16;
    private const int IPv6PrefixBits = 64;
    private const int IPv6NetworkBytes = IPv6PrefixBits / 8;
    private const int MaxTrackedClients = 1000;
    private const string OverflowClient = "overflow";

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, List<DateTimeOffset>> _byClient = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _overall = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// The key a client is counted under: its IPv4 address, or the /64 network of an IPv6 address. One subscriber
    /// usually controls a whole /64, so counting single IPv6 addresses would give an attacker a fresh budget per address.
    /// </summary>
    public static string ClientKeyFor(IPAddress? address)
    {
        if (address is null)
        {
            return UnknownClient;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[IPv6AddressBytes];
        address.TryWriteBytes(bytes, out _);
        bytes[IPv6NetworkBytes..].Clear();
        return $"{new IPAddress(bytes)}/{IPv6PrefixBits}";
    }

    /// <summary>Counts an attempt unless the client or the server as a whole is over the limit.</summary>
    public ThrottleDecision TryBegin(string client)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            Prune(now);

            // Bound the memory an attacker can make us spend on distinct addresses.
            var key = _byClient.ContainsKey(client) || _byClient.Count < MaxTrackedClients ? client : OverflowClient;
            if (!_byClient.TryGetValue(key, out var mine))
            {
                mine = [];
                _byClient[key] = mine;
            }

            if (mine.Count >= MaxAttemptsPerClient)
            {
                return ThrottleDecision.Denied(mine[0] + Window - now);
            }

            if (_overall.Count >= MaxAttemptsOverall)
            {
                return ThrottleDecision.Denied(_overall[0] + Window - now);
            }

            mine.Add(now);
            _overall.Add(now);
            return ThrottleDecision.Allowed(new Attempt(key, now));
        }
    }

    /// <summary>Gives the attempt back: a correct password must not eat into the limit.</summary>
    public void Refund(Attempt attempt)
    {
        lock (_gate)
        {
            if (_byClient.TryGetValue(attempt.Client, out var mine))
            {
                mine.Remove(attempt.At);
            }

            _overall.Remove(attempt.At);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - Window;
        foreach (var (client, attempts) in _byClient.ToList())
        {
            attempts.RemoveAll(at => at < cutoff);
            if (attempts.Count == 0)
            {
                _byClient.Remove(client);
            }
        }

        _overall.RemoveAll(at => at < cutoff);
    }
}

/// <summary>One counted login attempt, kept so it can be refunded.</summary>
internal readonly record struct Attempt(string Client, DateTimeOffset At);

internal readonly record struct ThrottleDecision(Attempt? Attempt, TimeSpan RetryAfter)
{
    public bool IsAllowed => Attempt is not null;

    public static ThrottleDecision Allowed(Attempt attempt) => new(attempt, TimeSpan.Zero);

    public static ThrottleDecision Denied(TimeSpan retryAfter) => new(null, retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter);
}
