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
}
