using Auth.Server.Lockout;

namespace Auth.IntegrationTests;

public sealed class LockoutPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HumanPace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>Registers <paramref name="attempts"/> attempts, <paramref name="spacing"/> apart, the first one that long after <paramref name="from"/>.</summary>
    private static (StreakState State, AttemptDecision Last, DateTimeOffset Now) Attempt(
        StreakState state, DateTimeOffset from, int attempts, TimeSpan spacing)
    {
        var now = from;
        AttemptDecision last = default;
        for (var i = 0; i < attempts; i++)
        {
            now += spacing;
            (state, last) = LockoutPolicy.Register(state, now);
        }

        return (state, last, now);
    }

    [Fact]
    public void First_nine_attempts_are_allowed_and_start_no_cooldown()
    {
        var (state, last, _) = Attempt(StreakState.Fresh(T0), T0, 9, HumanPace);

        Assert.True(last.Allowed);
        Assert.Equal(9, state.AttemptCount);
        Assert.Null(state.LockedUntil);
    }

    [Fact]
    public void Tenth_attempt_is_still_allowed_and_starts_a_one_minute_cooldown()   // criterion 1
    {
        var (state, last, now) = Attempt(StreakState.Fresh(T0), T0, 10, HumanPace);

        Assert.True(last.Allowed);
        Assert.Equal(now + Minute, state.LockedUntil);
    }

    [Fact]
    public void Attempt_during_a_cooldown_is_refused_and_adds_one_minute()   // criterion 3
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 10, HumanPace);

        (state, var first) = LockoutPolicy.Register(state, now);
        (state, var second) = LockoutPolicy.Register(state, now + TimeSpan.FromSeconds(30));

        Assert.False(first.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(2), first.RetryAfter);
        Assert.False(second.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(150), second.RetryAfter);   // 3 min from the lock, 30 s already gone
        Assert.Equal(12, state.AttemptCount);
    }

    [Fact]
    public void Remaining_cooldown_never_exceeds_thirty_minutes()   // criterion 3
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 10, HumanPace);

        AttemptDecision last = default;
        for (var i = 0; i < 50; i++)
        {
            (state, last) = LockoutPolicy.Register(state, now);
            Assert.True(last.RetryAfter <= LockoutPolicy.MaxCooldown, $"Attempt {i}: {last.RetryAfter}");
        }

        Assert.Equal(LockoutPolicy.MaxCooldown, last.RetryAfter);

        // Ten minutes later 20 are left; the attempt adds one.
        (_, var later) = LockoutPolicy.Register(state, now + TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(21), later.RetryAfter);
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(12, 3)]
    [InlineData(39, 30)]
    [InlineData(40, 30)]
    [InlineData(int.MaxValue, 30)]
    public void Cooldown_is_one_minute_at_the_threshold_plus_one_per_attempt_up_to_the_cap(int attemptCount, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), LockoutPolicy.CooldownFor(attemptCount));

    [Fact]
    public void Failure_after_an_expired_cooldown_starts_a_longer_one()   // criterion 9
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 10, HumanPace);
        (state, _) = LockoutPolicy.Register(state, now);
        (state, _) = LockoutPolicy.Register(state, now);   // attempts 11 and 12, both refused: locked until now + 3 min

        var expiry = now + TimeSpan.FromMinutes(3);
        (state, var decision) = LockoutPolicy.Register(state, expiry);

        Assert.True(decision.Allowed);                      // a lock ending exactly now has expired
        Assert.Equal(13, state.AttemptCount);
        Assert.Equal(expiry + TimeSpan.FromMinutes(4), state.LockedUntil);
    }

    [Fact]
    public void Streak_is_forgotten_after_24_hours_without_an_attempt()   // criterion 10
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 9, HumanPace);

        (state, var decision) = LockoutPolicy.Register(state, now + LockoutPolicy.StreakLifetime);

        Assert.True(decision.Allowed);
        Assert.Equal(1, state.AttemptCount);
        Assert.Null(state.LockedUntil);
    }

    [Fact]
    public void Streak_is_kept_just_under_24_hours()   // criterion 10
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 9, HumanPace);

        (state, _) = LockoutPolicy.Register(state, now + LockoutPolicy.StreakLifetime - TimeSpan.FromSeconds(1));

        Assert.Equal(10, state.AttemptCount);
        Assert.NotNull(state.LockedUntil);
    }

    [Fact]
    public void Attempt_count_does_not_overflow()
    {
        var saturated = new StreakState(int.MaxValue, T0, T0 - Minute, T0, 0);

        var (state, decision) = LockoutPolicy.Register(saturated, T0 + HumanPace);

        Assert.True(decision.Allowed);
        Assert.Equal(int.MaxValue, state.AttemptCount);
        Assert.Equal(T0 + HumanPace + LockoutPolicy.MaxCooldown, state.LockedUntil);
    }

    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(1);

    [Fact]
    public void Fifth_attempt_inside_ten_seconds_starts_the_cooldown_at_once()   // criterion 5
    {
        var (state, last, now) = Attempt(StreakState.Fresh(T0), T0, 5, Fast);

        Assert.True(last.Allowed);                                  // the fifth is still evaluated
        Assert.Equal(LockoutPolicy.Threshold, state.AttemptCount);  // raised to the threshold
        Assert.Equal(now + Minute, state.LockedUntil);

        (_, var sixth) = LockoutPolicy.Register(state, now + Fast);
        Assert.False(sixth.Allowed);
    }

    [Fact]
    public void Four_fast_attempts_do_not_lock()
    {
        var (state, _, _) = Attempt(StreakState.Fresh(T0), T0, 4, Fast);

        Assert.Equal(4, state.AttemptCount);
        Assert.Null(state.LockedUntil);
    }

    [Fact]
    public void Window_runs_ten_seconds_from_its_first_attempt()   // criterion 5
    {
        var (four, _, first) = Attempt(StreakState.Fresh(T0), T0 - Fast, 1, Fast);   // one attempt, at T0
        (four, _, _) = Attempt(four, first, 3, TimeSpan.Zero);                       // three more at T0

        var (inside, _) = LockoutPolicy.Register(four, T0 + TimeSpan.FromSeconds(9.999));
        var (outside, _) = LockoutPolicy.Register(four, T0 + LockoutPolicy.BurstWindow);

        Assert.NotNull(inside.LockedUntil);
        Assert.Null(outside.LockedUntil);
        Assert.Equal(5, outside.AttemptCount);
        Assert.Equal(1, outside.BurstCount);                        // a new window, opened by this attempt
    }

    [Fact]
    public void After_a_burst_lock_the_escalation_continues_from_the_threshold()   // Decision 9
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 5, Fast);

        var expiry = now + Minute;
        (state, var decision) = LockoutPolicy.Register(state, expiry);

        Assert.True(decision.Allowed);
        Assert.Equal(11, state.AttemptCount);
        Assert.Equal(expiry + TimeSpan.FromMinutes(2), state.LockedUntil);
    }

    [Fact]
    public void Refused_attempts_do_not_touch_the_window()
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 5, Fast);
        var burst = (state.BurstStartedAt, state.BurstCount);

        (state, _) = LockoutPolicy.Register(state, now + Fast);

        Assert.Equal(burst, (state.BurstStartedAt, state.BurstCount));
    }
    [Fact]
    public void Window_is_fixed_not_sliding()   // Decision 9
    {
        // One attempt at T0, three at T0+9 s: four in the window that opened at T0.
        var (state, _, first) = Attempt(StreakState.Fresh(T0), T0 - Fast, 1, Fast);
        (state, _) = LockoutPolicy.Register(state, first + TimeSpan.FromSeconds(9));
        (state, _) = LockoutPolicy.Register(state, first + TimeSpan.FromSeconds(9));
        (state, _) = LockoutPolicy.Register(state, first + TimeSpan.FromSeconds(9));

        // T0+10.5 s and T0+11 s: five attempts inside the last 10 seconds, but the window that opened at T0 has closed.
        (state, _) = LockoutPolicy.Register(state, first + TimeSpan.FromSeconds(10.5));
        (state, var decision) = LockoutPolicy.Register(state, first + TimeSpan.FromSeconds(11));

        Assert.True(decision.Allowed);
        Assert.Null(state.LockedUntil);
        Assert.Equal(6, state.AttemptCount);
        Assert.Equal(2, state.BurstCount);
    }
}
