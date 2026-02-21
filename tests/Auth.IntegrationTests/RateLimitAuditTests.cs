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
}
