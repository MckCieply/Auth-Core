using System.Collections.Concurrent;

namespace Auth.Server.RateLimiting;

/// <summary>
/// Decides which refusals of the limiter are written to the audit log: at most one per client address and policy per minute, so
/// that a flood cannot fill the table. In memory, like the limiter; entries older than a minute are forgotten once a minute.
/// </summary>
public sealed class RateLimitAudit(TimeProvider clock)
{
    private const long WindowMilliseconds = 60_000;

    private readonly ConcurrentDictionary<(string Address, RatePolicy Policy), long> _recorded = new();
    private long _lastSweep = clock.GetUtcNow().ToUnixTimeMilliseconds();

    public int Count => _recorded.Count;

    /// <summary><see langword="true"/> for the first refusal of this address and policy in the last minute.</summary>
    public bool ShouldRecord(string address, RatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(address);

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        SweepIfDue(now);
        var key = (address, policy);
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
                _recorded.TryRemove(new KeyValuePair<(string Address, RatePolicy Policy), long>(key, seen));
            }
        }
    }
}
