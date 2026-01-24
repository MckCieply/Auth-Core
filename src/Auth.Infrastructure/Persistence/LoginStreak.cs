namespace Auth.Infrastructure.Persistence;

/// <summary>
/// The lockout state of one login identifier (spec 0003): its attempts since the last successful login. The key is
/// a hash, never the submitted email, and a row exists for emails without an account too.
/// </summary>
public sealed class LoginStreak
{
    /// <summary>SHA-256 of the normalised email: 32 bytes whatever was submitted.</summary>
    public required byte[] IdentifierHash { get; init; }

    public int AttemptCount { get; set; }

    public DateTimeOffset LastAttemptAt { get; set; }

    /// <summary>End of the last cooldown started in this streak; <see langword="null"/> when there was none.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset BurstStartedAt { get; set; }

    public int BurstCount { get; set; }
}
