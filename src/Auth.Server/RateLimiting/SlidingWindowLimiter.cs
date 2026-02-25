using System.Collections.Concurrent;

namespace Auth.Server.RateLimiting;

/// <param name="Allowed">Whether the request is let through.</param>
/// <param name="RetryAfterSeconds">When it is refused: the seconds until the window holds fewer requests than the limit, at least 1.</param>
/// <param name="Overflow">The request was counted under the shared overflow partition (see <see cref="SlidingWindowLimiter"/>), not its own.</param>
public readonly record struct RateDecision(bool Allowed, int RetryAfterSeconds, bool Overflow = false);

/// <summary>
/// The limiter of spec 0008 (Decision 13): per policy and partition, a sliding window of one minute in six segments of ten
/// seconds, counted on the injected clock. A request is let through when the six segments add up to less than the limit. A
/// refused request counts nothing. When one is refused, the answer says how many seconds pass before the window holds fewer
/// requests than the limit, at least 1. The counters live in memory (one instance per product, ADR 0001): a restart clears them.
/// Time is the clock's monotonic timestamp, counted from the creation of the limiter, not its wall clock: a step of the wall clock
/// (NTP, a manual change) must not leave a partition blocked, or let one through early.
/// Partitions that have been idle for a whole window are removed once a minute, so memory follows the last minute's traffic.
/// The number of windows is capped (<see cref="DefaultMaxPartitions"/>): while it is at the cap, a request whose policy and partition
/// have no window yet is counted under one shared <see cref="OverflowPartition"/> of its policy instead of making a window, with the
/// policy's normal limit, so that a flood from more addresses than the cap limits itself (fail closed). Partitions that already have
/// a window keep their own counter; when the sweep brings the number below the cap, new partitions get their own windows again.
/// The cap is checked without a lock, so parallel requests that create windows together can pass it by at most their own number.
/// </summary>
public sealed class SlidingWindowLimiter
{
    /// <summary>The number of windows above which new partitions share one counter per policy.</summary>
    public const int DefaultMaxPartitions = 200_000;

    /// <summary>The partition, of every policy, that counts the requests of the partitions that came after the cap was reached.</summary>
    public const string OverflowPartition = "overflow";

    public const int Segments = 6;
    private const long SegmentMilliseconds = 10_000;
    private const long SweepIntervalMilliseconds = 60_000;

    private readonly TimeProvider _clock;
    private readonly int _maxPartitions;
    private readonly ConcurrentDictionary<(RatePolicy Policy, string Partition), Window> _windows = new();
    private readonly long _started;
    private long _lastSweep;
    private int _count;   // the number of windows, kept beside the dictionary because its Count takes every lock

    public SlidingWindowLimiter(TimeProvider clock)
        : this(clock, DefaultMaxPartitions)
    {
    }

    /// <summary>For the tests: a cap small enough to reach.</summary>
    internal SlidingWindowLimiter(TimeProvider clock, int maxPartitions)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPartitions, 1);
        _clock = clock;
        _maxPartitions = maxPartitions;
        _started = clock.GetTimestamp();
    }

    /// <summary>How many partitions are being counted (for the tests).</summary>
    public int PartitionCount => _windows.Count;

    public RateDecision TryAcquire(RatePolicy policy, string partition, int permitPerMinute)
    {
        ArgumentNullException.ThrowIfNull(partition);

        var now = ElapsedMilliseconds();
        SweepIfDue(now);
        while (true)
        {
            var overflow = false;
            if (!_windows.TryGetValue((policy, partition), out var window))
            {
                overflow = Volatile.Read(ref _count) >= _maxPartitions;
                var key = overflow ? (policy, OverflowPartition) : (policy, partition);
                window = _windows.TryGetValue(key, out var existing) ? existing : Add(key);
                if (window is null)
                {
                    continue; // another request added it, and it was swept at once: look again
                }
            }

            lock (window)
            {
                if (window.Removed)
                {
                    continue; // swept between the lookup and the lock: take a new one
                }

                return window.TryAcquire(now, permitPerMinute) with { Overflow = overflow };
            }
        }
    }

    /// <summary>Adds a window; the one that is there when another request was first, or <see langword="null"/> when that one is already gone.</summary>
    private Window? Add((RatePolicy Policy, string Partition) key)
    {
        var created = new Window();
        if (_windows.TryAdd(key, created))
        {
            Interlocked.Increment(ref _count);
            return created;
        }

        return _windows.TryGetValue(key, out var existing) ? existing : null;
    }

    /// <summary>
    /// Milliseconds since the limiter was made, on the timestamp clock. <c>GetElapsedTime</c> subtracts the two timestamps first
    /// and scales the difference by a floating-point ratio, so a timestamp of nanoseconds that has run for months cannot overflow
    /// the way <c>timestamp * 1000</c> would.
    /// </summary>
    private long ElapsedMilliseconds() => (long)_clock.GetElapsedTime(_started).TotalMilliseconds;

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
                    if (_windows.TryRemove(new KeyValuePair<(RatePolicy Policy, string Partition), Window>(key, window)))
                    {
                        Interlocked.Decrement(ref _count);
                    }
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
