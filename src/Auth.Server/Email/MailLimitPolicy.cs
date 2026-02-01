namespace Auth.Server.Email;

/// <summary>The mail limit of one address and kind, as the rules see it. A count of 0 means "no request yet".</summary>
public readonly record struct MailLimitState(DateTimeOffset WindowStartedAt, int WindowCount, DateTimeOffset LastAcceptedAt)
{
    public static MailLimitState Fresh(DateTimeOffset now) => new(now, 0, now);
}

/// <summary>Accept the request, or refuse it and say when one would be accepted.</summary>
public readonly record struct MailLimitDecision(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// The mail limit of spec 0004 as one pure transition: at most one accepted request per <see cref="MinimumGap"/>,
/// and at most <see cref="WindowLimit"/> per fixed <see cref="Window"/>, which opens at an accepted request that
/// falls outside any open window. A refused request changes nothing, so retrying never makes the wait longer.
/// </summary>
public static class MailLimitPolicy
{
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(60);

    public const int WindowLimit = 5;

    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public static (MailLimitState Next, MailLimitDecision Decision) Register(MailLimitState current, DateTimeOffset now)
    {
        var accepted = new MailLimitDecision(true, TimeSpan.Zero);
        if (current.WindowCount == 0)
        {
            return (new MailLimitState(now, 1, now), accepted);
        }

        var windowOpen = now - current.WindowStartedAt < Window;
        var gapWait = current.LastAcceptedAt + MinimumGap - now;
        var windowWait = windowOpen && current.WindowCount >= WindowLimit
            ? current.WindowStartedAt + Window - now
            : TimeSpan.Zero;
        var wait = gapWait > windowWait ? gapWait : windowWait;
        if (wait > TimeSpan.Zero)
        {
            return (current, new MailLimitDecision(false, wait));
        }

        return windowOpen
            ? (current with { WindowCount = current.WindowCount + 1, LastAcceptedAt = now }, accepted)
            : (new MailLimitState(now, 1, now), accepted);
    }
}
