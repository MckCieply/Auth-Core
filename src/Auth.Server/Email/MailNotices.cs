using Auth.Infrastructure.Persistence;
using Auth.Server.Lockout;

namespace Auth.Server.Email;

/// <summary>
/// The notices of spec 0012 → Mails: "provider connected", "provider disconnected" and "password changed". A notice is queued by the change that
/// causes it, on the change's own context and inside its transaction (never through <see cref="MailRequestStore"/>, and not counted by its
/// limit), and the caller saves and commits; <see cref="Notify"/> wakes the dispatcher after the commit. The three kinds share one limit per
/// address (Amendment A10): ten in a fixed hour opened by the first, with no gap, under the key <see cref="MailKind.NoticeLimit"/> in the store
/// of spec 0004's limits. Beyond it the change still happens: no row, and one warning naming the kind, never the address.
/// </summary>
public sealed partial class MailNotices(MailDispatchSignal signal, ILogger<MailNotices> logger)
{
    public static readonly MailLimitRule Rule = new(TimeSpan.Zero, 10, TimeSpan.FromHours(1));

    /// <summary>Adds the notice to <paramref name="db"/> when the limit lets it through; the caller saves. Returns whether it was added.</summary>
    /// <param name="provider">The provider's name for a provider notice (required there); <see langword="null"/> for "password changed".</param>
    public async Task<bool> QueueAsync(
        AuthDbContext db, MailKind kind, string normalizedEmail, string? provider, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        if (kind is not (MailKind.ProviderLinked or MailKind.ProviderUnlinked or MailKind.PasswordChanged))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a notice.");
        }

        if ((kind == MailKind.PasswordChanged) != (provider is null))
        {
            throw new ArgumentException("A provider notice names its provider, and only a provider notice does.", nameof(provider));
        }

        var decision = await MailLimits.RegisterAsync(db, LoginIdentifier.HashOf(normalizedEmail), MailKind.NoticeLimit, now, cancellationToken, Rule);
        if (!decision.Allowed)
        {
            LogLimitReached(logger, kind);
            return false;
        }

        db.MailRequests.Add(new MailRequest { Kind = kind, NormalizedEmail = normalizedEmail, Provider = provider, RequestedAt = now, NextAttemptAt = now });
        return true;
    }

    /// <summary>Wakes the dispatcher; called once the change's transaction has committed.</summary>
    public void Notify() => signal.Notify();

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Kind} mail was not queued: the address reached the notice limit of the hour.")]
    private static partial void LogLimitReached(ILogger logger, MailKind kind);
}
