using System.Collections.Concurrent;

namespace Auth.Server.RateLimiting;

/// <summary>
/// Decides which refusals of the limiter are written to the audit log: at most one per partition and policy per minute, so
/// that a flood cannot fill the table. The partition is the limiter's own (<see cref="Network.ClientAddress.PartitionOf(HttpContext)"/>:
/// an IPv4 address, or the /64 of an IPv6 one), so that every address of a /64 that is over the limit counts as one source; the row
/// itself still names the full address. In memory, like the limiter; entries older than a minute are forgotten once a minute.
/// Time is the clock's monotonic timestamp, counted from the creation of this object, as in <see cref="SlidingWindowLimiter"/>, not its
/// wall clock: a step of the wall clock (NTP, a manual change) must not suppress rows for hours, or stop the sweep.
/// </summary>
public sealed class RateLimitAudit(TimeProvider clock)
{
    private const long WindowMilliseconds = 60_000;

    private readonly ConcurrentDictionary<(string Partition, RatePolicy Policy), long> _recorded = new();
    private readonly long _started = clock.GetTimestamp();
    private long _lastSweep;

    public int Count => _recorded.Count;

    /// <summary><see langword="true"/> for the first refusal of this partition and policy in the last minute.</summary>
    public bool ShouldRecord(string partition, RatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(partition);

        var now = ElapsedMilliseconds();
        SweepIfDue(now);
        var key = (partition, policy);
        while (true)
        {
            if (_recorded.TryGetValue(key, out var seen))
            {
                if (now - seen < WindowMilliseconds)
                {
                    return false;
                }

                if (_recorded.TryUpdate(key, now, seen))
                {
                    return true;
                }
            }
            else if (_recorded.TryAdd(key, now))
            {
                return true;
            }
        }
    }

    /// <summary>Milliseconds since this object was made, on the timestamp clock (see <see cref="SlidingWindowLimiter"/>).</summary>
    private long ElapsedMilliseconds() => (long)clock.GetElapsedTime(_started).TotalMilliseconds;

    private void SweepIfDue(long now)
    {
        var last = Interlocked.Read(ref _lastSweep);
        if (now - last < WindowMilliseconds || Interlocked.CompareExchange(ref _lastSweep, now, last) != last)
        {
            return;
        }

        foreach (var (key, seen) in _recorded)
        {
            if (now - seen >= WindowMilliseconds)
            {
                _recorded.TryRemove(new KeyValuePair<(string Partition, RatePolicy Policy), long>(key, seen));
            }
        }
    }
}
