using System.Globalization;

namespace Auth.Server.Sessions;

/// <summary>The confirmation window of spec 0012: 5 minutes, the lifetime of a <c>reauth</c> ticket (Settled 2).</summary>
public static class ConfirmationWindow
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>The end of a window as <c>GET /auth/me</c> says it: UTC, ISO 8601 with seconds, <c>2026-05-04T18:35:00Z</c>.</summary>
    public static string Format(DateTimeOffset until) =>
        until.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>What a session records with its authorization (spec 0012 → Sessions): its start and the end of its window (null: closed).</summary>
public sealed record SessionValues(DateTimeOffset Start, DateTimeOffset? ConfirmedUntil)
{
    /// <summary>A session a sign-in starts: it begins at <paramref name="start"/> and is confirmed for five minutes from <paramref name="now"/>.</summary>
    public static SessionValues Opened(DateTimeOffset start, DateTimeOffset now) => new(start, now + ConfirmationWindow.Lifetime);
}
