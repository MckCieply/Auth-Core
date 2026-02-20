using System.Collections.Concurrent;

namespace Auth.Server.RateLimiting;

public readonly record struct RateDecision(bool Allowed, int RetryAfterSeconds);

/// <summary>
/// The limiter of spec 0008 (Decision 13): per policy and partition, a sliding window of one minute in six segments of ten
/// seconds, counted on the injected clock. A request is let through when the six segments add up to less than the limit. A
/// refused request counts nothing. When one is refused, the answer says how many seconds pass before the window holds fewer
/// requests than the limit, at least 1. The counters live in memory (one instance per product, ADR 0001): a restart clears them.
/// Partitions that have been idle for a whole window are removed once a minute, so memory follows the last minute's traffic.
/// </summary>
public sealed class SlidingWindowLimiter(TimeProvider clock)
{
    public const int Segments = 6;
    private const long SegmentMilliseconds = 10_000;
    private const long SweepIntervalMilliseconds = 60_000;

    private readonly ConcurrentDictionary<(RatePolicy Policy, string Partition), Window> _windows = new();
    private long _lastSweep = clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>How many partitions are being counted (for the tests).</summary>
    public int PartitionCount => _windows.Count;

    public RateDecision TryAcquire(RatePolicy policy, string partition, int permitPerMinute)
    {
        ArgumentNullException.ThrowIfNull(partition);

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        SweepIfDue(now);
        while (true)
        {
            var window = _windows.GetOrAdd((policy, partition), static _ => new Window());
            lock (window)
            {
                if (window.Removed)
                {
                    continue; // swept between the lookup and the lock: take a new one
                }

                return window.TryAcquire(now, permitPerMinute);
            }
        }
    }

    private void SweepIfDue(long now)
    {
        var last = Interlocked.Read(ref _lastSweep);
        if (now - last < SweepIntervalMilliseconds || Interlocked.CompareExchange(ref _lastSweep, now, last) != last)
        {
            return;
        }

        foreach (var (key, window) in _windows)
        {
            lock (window)
            {
                if (window.IsIdle(now))
                {
                    window.Removed = true;
                    _windows.TryRemove(new KeyValuePair<(RatePolicy Policy, string Partition), Window>(key, window));
                }
            }
        }
    }

    private sealed class Window
    {
        private readonly long[] _ids = new long[Segments];
        private readonly int[] _counts = new int[Segments];
        private long _newest = long.MinValue;

        public Window() => Array.Fill(_ids, long.MinValue);

        public bool Removed { get; set; }

        public bool IsIdle(long now) => _newest <= (now / SegmentMilliseconds) - Segments;

        public RateDecision TryAcquire(long now, int limit)
        {
            var current = now / SegmentMilliseconds;
            var total = 0;
            for (var i = 0; i < Segments; i++)
            {
                if (_ids[i] > current - Segments)
                {
                    total += _counts[i];
                }
            }

            if (total < limit)
            {
                var slot = (int)(current % Segments);
                if (_ids[slot] != current)
                {
                    _ids[slot] = current;
                    _counts[slot] = 0;
                }

                _counts[slot]++;
                _newest = current;
                return new RateDecision(true, 0);
            }

            // Refused. Segments leave the window oldest first; the answer is when enough of them have left.
            var remaining = total;
            for (var id = current - Segments + 1; id <= current; id++)
            {
                var slot = (int)(((id % Segments) + Segments) % Segments);
                if (_ids[slot] != id)
                {
                    continue;
                }

                remaining -= _counts[slot];
                if (remaining < limit)
                {
                    var leavesAt = (id + Segments) * SegmentMilliseconds;
                    return new RateDecision(false, (int)Math.Max(1, (leavesAt - now + 999) / 1000));
                }
            }

            return new RateDecision(false, Segments * (int)(SegmentMilliseconds / 1000)); // not reached for a limit of at least 1
        }
    }
}
