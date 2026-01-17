namespace Auth.Server.Sessions;

/// <summary>
/// How long a session lives. Constants on purpose (spec 0002, Decision 10): they are part of the contract, not knobs.
/// </summary>
public static class SessionPolicy
{
    /// <summary>A refresh token is usable this long after it was issued; each use issues a fresh one.</summary>
    public static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(14);

    /// <summary>No session outlives this, however active it is.</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);

    /// <summary>A just-consumed refresh token is still accepted this long: an honest double-submit is not theft.</summary>
    public static readonly TimeSpan ReuseLeeway = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Claim holding when the session began (the login), in Unix seconds. It has no destination, so it lives only
    /// in the refresh token and never reaches an access token.
    /// </summary>
    public const string StartClaim = "session_start";

    /// <summary>
    /// Lifetime of the next refresh token: the sliding window, cut short so it never crosses the absolute cap.
    /// <see langword="null"/> once the cap is reached: the session is over.
    /// </summary>
    public static TimeSpan? RemainingLifetime(DateTimeOffset sessionStart, DateTimeOffset now)
    {
        var untilCap = sessionStart + AbsoluteLifetime - now;
        if (untilCap <= TimeSpan.Zero)
        {
            return null;
        }

        return untilCap < SlidingLifetime ? untilCap : SlidingLifetime;
    }
}
