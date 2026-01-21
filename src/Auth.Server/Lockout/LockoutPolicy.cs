namespace Auth.Server.Lockout;

/// <summary>The lockout state of one identifier, as the rules see it.</summary>
public readonly record struct StreakState(
    int AttemptCount, DateTimeOffset LastAttemptAt, DateTimeOffset? LockedUntil, DateTimeOffset BurstStartedAt, int BurstCount)
{
    /// <summary>An identifier without a streak.</summary>
    public static StreakState Fresh(DateTimeOffset now) => new(0, now, null, now, 0);
}

/// <summary>What to do with one attempt: evaluate its password, or refuse it and say for how long.</summary>
public readonly record struct AttemptDecision(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// The lockout rules of spec 0003 as one pure transition. An attempt is counted when it arrives, before its password
/// is looked at (Decision 14); a successful login then removes the state altogether, so a streak is the attempts
/// since the last success.
/// </summary>
public static class LockoutPolicy
{
    /// <summary>The attempt that starts the first cooldown.</summary>
    public const int Threshold = 10;

    public static readonly TimeSpan BaseCooldown = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CooldownStep = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(30);

    /// <summary>A streak without an attempt for this long is forgotten.</summary>
    public static readonly TimeSpan StreakLifetime = TimeSpan.FromHours(24);

    public static (StreakState Next, AttemptDecision Decision) Register(StreakState current, DateTimeOffset now)
    {
        var state = now - current.LastAttemptAt >= StreakLifetime ? StreakState.Fresh(now) : current;
        var count = state.AttemptCount == int.MaxValue ? int.MaxValue : state.AttemptCount + 1;

        if (state.LockedUntil is { } until && until > now)
        {
            // Refused whatever the password is, and the cooldown grows; the time left never passes the cap.
            var cap = now + MaxCooldown;
            var extended = until + CooldownStep < cap ? until + CooldownStep : cap;
            return (state with { AttemptCount = count, LastAttemptAt = now, LockedUntil = extended },
                new AttemptDecision(false, extended - now));
        }

        // This attempt is evaluated. From the threshold on it also starts a cooldown, which only a correct password
        // (the caller then clears the streak) keeps from applying to the next attempt.
        var lockedUntil = count >= Threshold ? now + CooldownFor(count) : (DateTimeOffset?)null;
        return (state with { AttemptCount = count, LastAttemptAt = now, LockedUntil = lockedUntil },
            new AttemptDecision(true, TimeSpan.Zero));
    }

    /// <summary>The cooldown started by attempt number <paramref name="attemptCount"/> of a streak: min(30, n − 9) minutes.</summary>
    public static TimeSpan CooldownFor(int attemptCount)
    {
        var maxSteps = (int)((MaxCooldown - BaseCooldown) / CooldownStep);
        var steps = Math.Clamp(attemptCount - Threshold, 0, maxSteps);
        return BaseCooldown + (CooldownStep * steps);
    }
}
