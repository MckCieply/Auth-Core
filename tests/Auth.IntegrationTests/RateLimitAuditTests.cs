using Auth.Server.RateLimiting;
using Microsoft.Extensions.Time.Testing;

namespace Auth.IntegrationTests;

public sealed class RateLimitAuditTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(1_767_225_600));
    private readonly RateLimitAudit _audit;

    public RateLimitAuditTests() => _audit = new RateLimitAudit(_clock);

    [Fact]
    public void An_address_and_policy_are_recorded_once_a_minute()
    {
        Assert.True(_audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
        Assert.False(_audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(_audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
    }

    [Fact]
    public void Another_address_or_policy_is_its_own_event()
    {
        Assert.True(_audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
        Assert.True(_audit.ShouldRecord("203.0.113.10", RatePolicy.Login));
        Assert.True(_audit.ShouldRecord("203.0.113.9", RatePolicy.Refresh));
    }

    [Fact]
    public void Old_entries_are_forgotten_so_that_memory_stays_bounded()
    {
        for (var i = 0; i < 500; i++)
        {
            Assert.True(_audit.ShouldRecord("198.51.100." + (i % 250) + "/" + i, RatePolicy.General));
        }

        Assert.Equal(500, _audit.Count);

        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(_audit.ShouldRecord("203.0.113.9", RatePolicy.Login));

        Assert.Equal(1, _audit.Count);
    }

    [Fact]
    public void Requests_that_arrive_together_record_one_event()
    {
        var recorded = 0;

        Parallel.For(0, 200, _ =>
        {
            if (_audit.ShouldRecord("203.0.113.9", RatePolicy.Login))
            {
                Interlocked.Increment(ref recorded);
            }
        });

        Assert.Equal(1, recorded);
    }

    [Fact]
    public void A_wall_clock_set_back_does_not_suppress_rows_past_a_minute()   // an NTP step, a manual change
    {
        var clock = new SteppedClock();
        var audit = new RateLimitAudit(clock);
        Assert.True(audit.ShouldRecord("203.0.113.9", RatePolicy.Login));

        clock.Wall -= TimeSpan.FromHours(1);    // the step back
        clock.Pass(TimeSpan.FromSeconds(61));   // a minute of real time goes by; the wall clock is still an hour behind where it was

        Assert.True(audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
    }

    [Fact]
    public void A_wall_clock_set_back_does_not_stop_the_sweep()
    {
        var clock = new SteppedClock();
        var audit = new RateLimitAudit(clock);
        Assert.True(audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
        Assert.True(audit.ShouldRecord("203.0.113.10", RatePolicy.Login));

        clock.Wall -= TimeSpan.FromDays(1);
        clock.Pass(TimeSpan.FromMinutes(5));
        Assert.True(audit.ShouldRecord("203.0.113.11", RatePolicy.Login));

        Assert.Equal(1, audit.Count);
    }

    [Fact]
    public void A_wall_clock_set_forward_does_not_record_early()
    {
        var clock = new SteppedClock();
        var audit = new RateLimitAudit(clock);
        Assert.True(audit.ShouldRecord("203.0.113.9", RatePolicy.Login));

        clock.Wall += TimeSpan.FromHours(1);

        Assert.False(audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
    }

    /// <summary>A clock whose wall time can be set to anything, while its timestamp only ever rises: what a real machine does.</summary>
    private sealed class SteppedClock : TimeProvider
    {
        private long _milliseconds;

        public DateTimeOffset Wall { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_767_225_600);

        public override DateTimeOffset GetUtcNow() => Wall;

        public override long GetTimestamp() => _milliseconds;

        public override long TimestampFrequency => 1_000;

        public void Pass(TimeSpan span)
        {
            _milliseconds += (long)span.TotalMilliseconds;
            Wall += span;
        }
    }
}
