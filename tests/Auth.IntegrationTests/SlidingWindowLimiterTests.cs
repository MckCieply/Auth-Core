using Auth.Server.RateLimiting;
using Microsoft.Extensions.Time.Testing;

namespace Auth.IntegrationTests;

public sealed class SlidingWindowLimiterTests
{
    // 2026-01-01T00:00:00Z: a multiple of ten seconds, so a segment starts exactly here.
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_767_225_600);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly SlidingWindowLimiter _limiter;

    public SlidingWindowLimiterTests() => _limiter = new SlidingWindowLimiter(_clock);

    private RateDecision Try(int limit, string partition = "a", RatePolicy policy = RatePolicy.Login) =>
        _limiter.TryAcquire(policy, partition, limit);

    [Fact]
    public void Exactly_the_limit_is_let_through_and_the_next_request_is_refused()   // criterion 1
    {
        for (var i = 0; i < 30; i++)
        {
            Assert.True(Try(30).Allowed, $"request {i + 1}");
        }

        var refused = Try(30);

        Assert.False(refused.Allowed);
        Assert.Equal(60, refused.RetryAfterSeconds);   // the first segment leaves the window 60 s after it began
    }

    [Fact]
    public void The_wait_shrinks_with_the_clock()
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.True(Try(3).Allowed);
        }

        _clock.Advance(TimeSpan.FromSeconds(25));

        Assert.Equal(35, Try(3).RetryAfterSeconds);
    }

    [Fact]
    public void The_wait_is_the_time_until_the_window_holds_fewer_requests_than_the_limit()
    {
        Assert.True(Try(3).Allowed);              // segment 0
        Assert.True(Try(3).Allowed);              // segment 0
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(Try(3).Allowed);              // segment 3

        _clock.Advance(TimeSpan.FromSeconds(1));
        var refused = Try(3);

        Assert.False(refused.Allowed);
        Assert.Equal(29, refused.RetryAfterSeconds);   // dropping segment 0 leaves one request: below 3, at +60 s

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(Try(3).Allowed);
    }

    [Fact]
    public void The_wait_is_never_less_than_one_second()
    {
        for (var i = 0; i < 2; i++)
        {
            Assert.True(Try(2).Allowed);
        }

        _clock.Advance(TimeSpan.FromMilliseconds(59_999));

        Assert.Equal(1, Try(2).RetryAfterSeconds);
    }

    [Fact]
    public void A_refused_request_is_not_counted()   // criterion 1
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.True(Try(3).Allowed);
        }

        for (var i = 0; i < 100; i++)
        {
            Assert.False(Try(3).Allowed);
        }

        _clock.Advance(TimeSpan.FromSeconds(60));

        Assert.True(Try(3).Allowed);   // had the refusals counted, the window would still be full
        Assert.True(Try(3).Allowed);
        Assert.True(Try(3).Allowed);
        Assert.False(Try(3).Allowed);
    }

    [Fact]
    public void Requests_in_the_last_minute_count_and_older_ones_do_not()
    {
        Assert.True(Try(2).Allowed);
        _clock.Advance(TimeSpan.FromSeconds(50));
        Assert.True(Try(2).Allowed);
        Assert.False(Try(2).Allowed);   // both are inside the last minute

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(Try(2).Allowed);    // the first one has left
        Assert.False(Try(2).Allowed);
    }

    [Fact]
    public void Partitions_and_policies_are_counted_apart()
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.True(Try(3, "a").Allowed);
        }

        Assert.False(Try(3, "a").Allowed);
        Assert.True(Try(3, "b").Allowed);
        Assert.True(Try(3, "a", RatePolicy.Refresh).Allowed);
    }

    [Fact]
    public void Idle_partitions_are_forgotten()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.True(Try(3, "partition-" + i).Allowed);
        }

        Assert.Equal(100, _limiter.PartitionCount);

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(Try(3, "newcomer").Allowed);

        Assert.Equal(1, _limiter.PartitionCount);
    }

    [Fact]
    public void A_partition_that_is_still_in_use_is_not_forgotten()
    {
        Assert.True(Try(3, "busy").Allowed);
        _clock.Advance(TimeSpan.FromSeconds(55));
        Assert.True(Try(3, "busy").Allowed);
        _clock.Advance(TimeSpan.FromSeconds(10));   // the sweep runs on this call, and the second request is still in the window

        Assert.True(Try(3, "busy").Allowed);
        Assert.True(Try(3, "busy").Allowed);
        Assert.False(Try(3, "busy").Allowed);
    }

    /// <summary>A clock whose wall time can be set to anything, while its timestamp only ever rises: what a real machine does.</summary>
    private sealed class SteppedClock : TimeProvider
    {
        private long _milliseconds;

        public DateTimeOffset Wall { get; set; } = Start;

        public override DateTimeOffset GetUtcNow() => Wall;

        public override long GetTimestamp() => _milliseconds;

        public override long TimestampFrequency => 1_000;

        public void Pass(TimeSpan span)
        {
            _milliseconds += (long)span.TotalMilliseconds;
            Wall += span;
        }
    }

    [Fact]
    public void A_wall_clock_set_back_does_not_keep_a_partition_blocked()   // an NTP step, a manual change
    {
        var clock = new SteppedClock();
        var limiter = new SlidingWindowLimiter(clock);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire(RatePolicy.Login, "a", 3).Allowed);
        }

        Assert.False(limiter.TryAcquire(RatePolicy.Login, "a", 3).Allowed);

        clock.Wall -= TimeSpan.FromHours(1);    // the step back
        clock.Pass(TimeSpan.FromSeconds(61));   // a minute of real time goes by; the wall clock is still an hour behind where it was

        Assert.True(limiter.TryAcquire(RatePolicy.Login, "a", 3).Allowed);
    }

    [Fact]
    public void A_wall_clock_set_back_does_not_stop_idle_partitions_from_being_forgotten()
    {
        var clock = new SteppedClock();
        var limiter = new SlidingWindowLimiter(clock);
        Assert.True(limiter.TryAcquire(RatePolicy.Login, "gone", 3).Allowed);

        clock.Wall -= TimeSpan.FromDays(1);
        clock.Pass(TimeSpan.FromMinutes(5));
        Assert.True(limiter.TryAcquire(RatePolicy.Login, "newcomer", 3).Allowed);

        Assert.Equal(1, limiter.PartitionCount);
    }

    [Fact]
    public void A_wall_clock_set_forward_does_not_let_a_partition_through_early()
    {
        var clock = new SteppedClock();
        var limiter = new SlidingWindowLimiter(clock);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire(RatePolicy.Login, "a", 3).Allowed);
        }

        clock.Wall += TimeSpan.FromHours(1);

        Assert.False(limiter.TryAcquire(RatePolicy.Login, "a", 3).Allowed);
    }

    [Fact]
    public void Requests_that_arrive_together_cannot_pass_the_limit()
    {
        var allowed = 0;

        Parallel.For(0, 400, _ =>
        {
            if (Try(50).Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(50, allowed);
    }
}
