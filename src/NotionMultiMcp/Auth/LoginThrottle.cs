namespace NotionMultiMcp.Auth;

/// <summary>
/// Limits password guesses. Each client address gets <see cref="MaxAttemptsPerClient"/> attempts per
/// <see cref="Window"/>, and the server as a whole gets <see cref="MaxAttemptsOverall"/>, so one attacker cannot
/// lock the owner out of the login but a distributed one is still slowed down.
/// </summary>
/// <remarks>
/// An attempt is counted when it starts, under a lock, and refunded when the password turns out to be right, so
/// a burst of concurrent requests cannot all pass the check before any failure is recorded. The state is in
/// memory and per process (see ADR 0003): it resets when the app scales to zero, so it slows guessing down but
/// does not replace a long, random password.
/// </remarks>
internal sealed class LoginThrottle(TimeProvider timeProvider)
{
    public const int MaxAttemptsPerClient = 10;
    public const int MaxAttemptsOverall = 60;

    private const int MaxTrackedClients = 1000;
    private const string OverflowClient = "overflow";

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, List<DateTimeOffset>> _byClient = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _overall = [];
    private readonly Lock _gate = new();

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
