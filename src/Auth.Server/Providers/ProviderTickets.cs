using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Providers;

/// <summary>What a callback stores: the purpose, the identity and, for an invitation, the hash of its link token.</summary>
public sealed record ProviderTicketDraft(ProviderTicketPurpose Purpose, ProviderIdentity Identity, byte[]? InviteTokenHash);

/// <summary>
/// A new ticket and the value of its <c>auth_pt</c> cookie, both in clear: the callback puts the first into the fragment and the second into the
/// cookie, and nothing stores either.
/// </summary>
public sealed record IssuedTicket(string Ticket, string Binding);

/// <summary>
/// The provider tickets of spec 0011: 32 random bytes as 43 base64url characters, like the link tokens of spec 0004, stored as SHA-256 only, with
/// the SHA-256 of the browser's binding cookie beside them. Every operation runs in the caller's context, so the caller's transaction decides
/// whether it happened: a sign-in that is rolled back gives the ticket back.
/// </summary>
public static class ProviderTickets
{
    /// <summary>A ticket of a sign-in or an invitation lives 10 minutes after the callback.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>A ticket of a re-authentication lives 5 minutes.</summary>
    public static readonly TimeSpan ReauthLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The state of a round trip (OpenIddict's) lives 10 minutes, from the start to the callback.</summary>
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public static TimeSpan LifetimeOf(ProviderTicketPurpose purpose) =>
        purpose == ProviderTicketPurpose.Reauth ? ReauthLifetime : Lifetime;

    /// <summary>Adds the ticket and saves it at once; returns the ticket and the binding in clear.</summary>
    public static async Task<IssuedTicket> IssueAsync(AuthDbContext db, ProviderTicketDraft draft, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(draft);

        var ticket = EmailTokens.NewToken();
        var binding = EmailTokens.NewToken();
        db.ProviderTickets.Add(new ProviderTicket
        {
            Id = Guid.NewGuid(),
            TicketHash = EmailTokens.HashOf(ticket),
            BindingHash = EmailTokens.HashOf(binding),
            Purpose = draft.Purpose,
            Provider = draft.Identity.Provider,
            IdentityKey = draft.Identity.Key,
            Email = draft.Identity.Email,
            EmailVerified = draft.Identity.EmailVerified,
            InviteTokenHash = draft.InviteTokenHash,
            ExpiresAt = now + LifetimeOf(draft.Purpose),
        });
        await db.SaveChangesAsync(cancellationToken);
        return new IssuedTicket(ticket, binding);
    }

    /// <summary>
    /// The live ticket of this browser, of any purpose; <see langword="null"/> for a ticket that is unknown, used or expired, and for one presented
    /// without its binding or with another browser's (spec 0011: one answer for all of them). It changes nothing.
    /// </summary>
    public static Task<ProviderTicket?> FindAsync(AuthDbContext db, string ticket, string? binding, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ticket);

        if (binding is null)
        {
            return Task.FromResult<ProviderTicket?>(null);
        }

        var hash = EmailTokens.HashOf(ticket);
        var bindingHash = EmailTokens.HashOf(binding);
        return db.ProviderTickets.AsNoTracking()
            .Where(t => t.TicketHash == hash && t.BindingHash == bindingHash && t.ExpiresAt > now)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Uses the ticket up: removes it if it is this browser's, of this purpose and live, and returns it; <see langword="null"/> otherwise. One
    /// statement, so of parallel requests with one ticket exactly one gets it; the others wait for its row and then find nothing.
    /// </summary>
    public static async Task<ProviderTicket?> ConsumeAsync(
        AuthDbContext db, string ticket, string binding, ProviderTicketPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(binding);

        var hash = EmailTokens.HashOf(ticket);
        var bindingHash = EmailTokens.HashOf(binding);
        var purposeValue = (short)purpose;

        // ToListAsync, not SingleOrDefaultAsync: EF must send the statement as it is, not wrapped in a subquery.
        var removed = await db.ProviderTickets
            .FromSql($"""
                DELETE FROM "ProviderTickets"
                WHERE "TicketHash" = {hash} AND "BindingHash" = {bindingHash} AND "Purpose" = {purposeValue} AND "ExpiresAt" > {now}
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return removed.Count == 1 ? removed[0] : null;
    }
}
