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

    // --- The cap on the number of windows (verification round 1, V3-F2) ---

    private SlidingWindowLimiter Capped(int cap) => new(_clock, cap);

    [Fact]
    public void Below_the_cap_every_partition_has_a_counter_of_its_own()
    {
        var limiter = Capped(5);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire(RatePolicy.Login, "p" + i, 1).Allowed, "p" + i);
        }

        Assert.False(limiter.TryAcquire(RatePolicy.Login, "p0", 1).Allowed);   // p0 used its one request; the others did not touch it
        Assert.Equal(5, limiter.PartitionCount);
        Assert.False(limiter.TryAcquire(RatePolicy.Login, "p1", 1).Overflow);
    }

    [Fact]
    public void At_the_cap_new_partitions_share_one_counter_with_the_normal_limit()
    {
        var limiter = Capped(3);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire(RatePolicy.Login, "old" + i, 2).Allowed);
        }

        var first = limiter.TryAcquire(RatePolicy.Login, "new-a", 2);
        var second = limiter.TryAcquire(RatePolicy.Login, "new-b", 2);
        var third = limiter.TryAcquire(RatePolicy.Login, "new-c", 2);

        Assert.True(first.Allowed);
        Assert.True(first.Overflow);
        Assert.True(second.Allowed);   // two requests in all, from two partitions: the limit of the policy
        Assert.True(second.Overflow);
        Assert.False(third.Allowed);   // the third partition finds the shared counter full
        Assert.True(third.Overflow);
        Assert.True(third.RetryAfterSeconds >= 1);
        Assert.False(limiter.TryAcquire(RatePolicy.Login, "new-a", 2).Allowed);   // and the first one is still counted with them
        Assert.Equal(4, limiter.PartitionCount);   // three own windows and the overflow window: no window was made for the new ones
    }

    [Fact]
    public void A_partition_that_has_a_window_is_not_moved_to_the_overflow_counter()
    {
        var limiter = Capped(2);
        Assert.True(limiter.TryAcquire(RatePolicy.Login, "old-a", 3).Allowed);
        Assert.True(limiter.TryAcquire(RatePolicy.Login, "old-b", 3).Allowed);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire(RatePolicy.Login, "new" + i, 3).Allowed);   // fills the overflow counter
        }

        Assert.False(limiter.TryAcquire(RatePolicy.Login, "new-late", 3).Allowed);

        var old = limiter.TryAcquire(RatePolicy.Login, "old-a", 3);   // its own counter holds one request of three
        Assert.True(old.Allowed);
        Assert.False(old.Overflow);
        Assert.True(limiter.TryAcquire(RatePolicy.Login, "old-a", 3).Allowed);
        Assert.False(limiter.TryAcquire(RatePolicy.Login, "old-a", 3).Allowed);   // and refuses at its own limit
    }

    [Fact]
    public void The_overflow_counter_is_one_for_each_policy()
    {
        var limiter = Capped(1);
        Assert.True(limiter.TryAcquire(RatePolicy.Login, "old", 1).Allowed);

        Assert.True(limiter.TryAcquire(RatePolicy.Login, "x", 1).Allowed);
        Assert.False(limiter.TryAcquire(RatePolicy.Login, "y", 1).Allowed);
        Assert.True(limiter.TryAcquire(RatePolicy.Refresh, "x", 1).Allowed);   // the overflow counter of another policy
        Assert.False(limiter.TryAcquire(RatePolicy.Refresh, "y", 1).Allowed);
    }

    [Fact]
    public void Once_the_sweep_has_brought_the_count_down_new_partitions_have_their_own_counters_again()
    {
        var limiter = Capped(3);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire(RatePolicy.Login, "old" + i, 1).Allowed);
        }

        Assert.True(limiter.TryAcquire(RatePolicy.Login, "overflowing", 1).Overflow);
        Assert.Equal(4, limiter.PartitionCount);

        _clock.Advance(TimeSpan.FromMinutes(2));   // everything is idle: the next call sweeps it all away

        var a = limiter.TryAcquire(RatePolicy.Login, "a", 1);
        var b = limiter.TryAcquire(RatePolicy.Login, "b", 1);
        Assert.True(a.Allowed);
        Assert.False(a.Overflow);
        Assert.True(b.Allowed);   // with a shared counter of 1, b would have been refused
        Assert.False(b.Overflow);
        Assert.Equal(2, limiter.PartitionCount);
    }

    [Fact]
    public void The_count_falls_by_the_windows_the_sweep_removes_so_the_cap_is_not_stuck()
    {
        var limiter = Capped(2);
        Assert.True(limiter.TryAcquire(RatePolicy.Login, "a", 5).Allowed);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(limiter.TryAcquire(RatePolicy.Login, "b", 5).Allowed);
        _clock.Advance(TimeSpan.FromSeconds(31));   // a is idle for the whole window, b is not: only a goes

        Assert.False(limiter.TryAcquire(RatePolicy.Login, "c", 5).Overflow);   // room for one more window
        Assert.True(limiter.TryAcquire(RatePolicy.Login, "d", 5).Overflow);    // and now it is full again
        Assert.Equal(3, limiter.PartitionCount);
    }

    [Fact]
    public void Windows_made_in_parallel_near_the_cap_pass_it_by_at_most_the_number_of_threads()
    {
        const int Cap = 200;
        const int Threads = 8;
        var limiter = Capped(Cap);
        var overflowAllowed = 0;

        Parallel.For(0, 4_000, new ParallelOptions { MaxDegreeOfParallelism = Threads }, i =>
        {
            var decision = limiter.TryAcquire(RatePolicy.Login, "p" + i, 50);
            if (decision.Overflow && decision.Allowed)
            {
                Interlocked.Increment(ref overflowAllowed);
            }
        });

        // The cap is read without a lock: each thread can add one window after the count reached it. Then one overflow window.
        Assert.InRange(limiter.PartitionCount, Cap, Cap + Threads + 1);
        Assert.Equal(50, overflowAllowed);   // however the requests interleave, the shared counter lets through exactly its limit
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
