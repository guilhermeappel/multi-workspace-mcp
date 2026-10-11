using Microsoft.Extensions.Time.Testing;
using NotionMultiMcp.Auth;

namespace NotionMultiMcp.Tests;

public sealed class LoginThrottleTests
{
    private const string Alice = "203.0.113.1";
    private const string Bob = "203.0.113.2";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private LoginThrottle CreateThrottle() => new(_time);

    private static void Attempt(LoginThrottle throttle, string client, int times)
    {
        for (var i = 0; i < times; i++)
        {
            Assert.True(throttle.TryBegin(client).IsAllowed);
        }
    }

    [Fact]
    public void A_client_gets_ten_attempts_and_the_eleventh_is_denied()
    {
        var throttle = CreateThrottle();

        Attempt(throttle, Alice, LoginThrottle.MaxAttemptsPerClient);

        Assert.False(throttle.TryBegin(Alice).IsAllowed);
    }

    [Fact]
    public void One_clients_attempts_do_not_lock_another_client_out()
    {
        var throttle = CreateThrottle();
        Attempt(throttle, Alice, LoginThrottle.MaxAttemptsPerClient);

        Assert.False(throttle.TryBegin(Alice).IsAllowed);
        Assert.True(throttle.TryBegin(Bob).IsAllowed);
    }

    [Fact]
    public void The_server_as_a_whole_is_limited_too()
    {
        var throttle = CreateThrottle();
        for (var i = 0; i < LoginThrottle.MaxAttemptsOverall / LoginThrottle.MaxAttemptsPerClient; i++)
        {
            Attempt(throttle, $"client-{i}", LoginThrottle.MaxAttemptsPerClient);
        }

        Assert.False(throttle.TryBegin("a-new-client").IsAllowed);
    }

    [Fact]
    public void A_denied_attempt_says_when_to_try_again()
    {
        var throttle = CreateThrottle();
        Attempt(throttle, Alice, LoginThrottle.MaxAttemptsPerClient);

        _time.Advance(TimeSpan.FromMinutes(5));
        var denied = throttle.TryBegin(Alice);

        Assert.False(denied.IsAllowed);
        Assert.Equal(TimeSpan.FromMinutes(10), denied.RetryAfter);
    }

    [Fact]
    public void Attempts_are_allowed_again_once_they_leave_the_window()
    {
        var throttle = CreateThrottle();
        Attempt(throttle, Alice, LoginThrottle.MaxAttemptsPerClient);

        _time.Advance(LoginThrottle.Window - TimeSpan.FromSeconds(1));
        Assert.False(throttle.TryBegin(Alice).IsAllowed);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(throttle.TryBegin(Alice).IsAllowed);
    }

    [Fact]
    public void Only_attempts_inside_the_window_count()
    {
        var throttle = CreateThrottle();
        Attempt(throttle, Alice, 6);

        _time.Advance(LoginThrottle.Window + TimeSpan.FromMinutes(1));
        Attempt(throttle, Alice, 6);

        Assert.True(throttle.TryBegin(Alice).IsAllowed);
    }

    [Fact]
    public void A_refunded_attempt_does_not_count()
    {
        var throttle = CreateThrottle();
        Attempt(throttle, Alice, LoginThrottle.MaxAttemptsPerClient - 1);
        var correctPassword = throttle.TryBegin(Alice);

        throttle.Refund(correctPassword.Attempt!.Value);

        // 9 counted attempts again: one more is allowed, the one after it is not.
        Assert.True(throttle.TryBegin(Alice).IsAllowed);
        Assert.False(throttle.TryBegin(Alice).IsAllowed);
    }

    [Fact]
    public void Refunding_also_frees_the_server_wide_budget()
    {
        var throttle = CreateThrottle();
        for (var i = 0; i < LoginThrottle.MaxAttemptsOverall / LoginThrottle.MaxAttemptsPerClient; i++)
        {
            Attempt(throttle, $"client-{i}", LoginThrottle.MaxAttemptsPerClient - 1);
        }

        var last = throttle.TryBegin("last-client");
        Assert.True(last.IsAllowed);
        throttle.Refund(last.Attempt!.Value);

        Assert.True(throttle.TryBegin("another-client").IsAllowed);
    }

    [Fact]
    public void Many_distinct_clients_cannot_make_the_throttle_grow_without_bound()
    {
        var throttle = CreateThrottle();

        // The server-wide limit stops this long before 5,000 clients, so spread it over time as well.
        for (var i = 0; i < 5_000; i++)
        {
            throttle.TryBegin($"client-{i}");
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.True(throttle.TryBegin("still-works").IsAllowed);
    }

    [Fact]
    public async Task Concurrent_attempts_cannot_exceed_the_limit()
    {
        var throttle = CreateThrottle();

        var decisions = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => throttle.TryBegin(Alice))));

        Assert.Equal(LoginThrottle.MaxAttemptsPerClient, decisions.Count(d => d.IsAllowed));
    }
}
