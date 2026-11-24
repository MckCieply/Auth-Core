using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Login;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using OpenIddict.Abstractions;

namespace Auth.Server.Providers;

/// <summary>
/// <c>POST /auth/providers/connect</c> (spec 0012): the identity of a <c>connect</c> ticket becomes a login of the caller's account, inside the
/// confirmation window of this device. The checks run in the order of the contract: the body; the session and the membership; the ticket (a live
/// <c>connect</c> ticket of this browser); the window (the ticket stays usable); an identity that is this account's login already (<c>204</c>, the
/// ticket used up, nothing else); another account's (<c>409 provider_already_linked</c>); a login of this provider already
/// (<c>409 provider_in_use</c>); the ticket's address (none, unverified, another); then, in one transaction under the address lock where the session,
/// the membership, the window and steps 5 to 7 are read again, the login, <c>provider.linked</c> (<c>via</c> <c>settings</c>), the ticket used up and
/// the notice "provider connected". Sessions are not touched.
/// </summary>
public static class ProviderConnectEndpoint
{
    private const string TicketField = "ticket";

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, MembershipReader memberships, IOpenIddictTokenManager tokens, AuditLog audit, MailNotices notices,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(clock);

        // 1. A ticket that is a string; a blank one is unknown at step 3.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || !fields.TryAnyString(TicketField, out var ticketText))
        {
            return ApiResults.InvalidRequest();
        }

        // 2.
        var read = await SettingsCallers.ReadAsync(http, db, tokens, memberships, clock, http.RequestAborted);
        if (read.Caller is not { } caller)
        {
            return read.Refusal!;
        }

        // 3.
        ProviderBindingCookie.TryRead(http.Request, out var binding);
        var now = StorableTime.Now(clock);
        var ticket = await ProviderTickets.FindAsync(db, ticketText, binding, now, http.RequestAborted);
        if (ticket is not { Purpose: ProviderTicketPurpose.Connect })
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);
        }

        // 4. Nothing is used up: the ticket stays usable for a confirmation and a second try.
        if (!caller.Session.IsConfirmedAt(now))
        {
            return ApiResults.Error(TenancyErrors.ConfirmationRequired);
        }

        // 5. Connected already: the ticket is used up and nothing else is written (no row, no mail).
        if (await ProviderLogins.IsLoginOfAsync(db, caller.UserId, ticket.Provider, ticket.IdentityKey, http.RequestAborted))
        {
            return await UseUpAsync(http, db, ticketText, binding!, clock);
        }

        // 6 and 7.
        if (await RefusalOfLoginsAsync(db, caller.UserId, ticket, http.RequestAborted) is { } refusal)
        {
            return refusal;
        }

        // 8. The address: there is one, the provider proved it, and it is the account's.
        if (ticket.Email is not { } email)
        {
            return ApiResults.Error(TenancyErrors.NoEmail);
        }

        if (!ticket.EmailVerified)
        {
            return ApiResults.Error(TenancyErrors.EmailNotVerified);
        }

        if (!EmailInput.IsSameAddress(caller.Email, email))
        {
            return ApiResults.Error(TenancyErrors.AddressMismatch);
        }

        return await ConnectAsync(http, db, memberships, audit, notices, clock, caller, ticket, ticketText, binding!);
    }

    /// <summary>
    /// 9. Not the request's token from here on: a client that goes away must not leave the connect half done. The address lock first, then the lock
    /// of the identity (another account with another address may hold a ticket of the same identity), then the reads.
    /// </summary>
    private static async Task<IResult> ConnectAsync(
        HttpContext http, AuthDbContext db, MembershipReader memberships, AuditLog audit, MailNotices notices, TimeProvider clock, SettingsCaller caller,
        ProviderTicket ticket, string ticketText, string binding)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await AddressLock.AcquireAsync(db, caller.NormalizedEmail, CancellationToken.None);
        await IdentityLock.AcquireAsync(db, ticket.Provider, ticket.IdentityKey, CancellationToken.None);   // after the address lock, always
        var now = StorableTime.Now(clock);   // after the lock: a ticket that ran out while this request waited is refused
        var again = await SettingsCallers.ReadAgainAsync(db, memberships, caller, now);
        if (again.Caller is not { } current)
        {
            return again.Refusal!;
        }

        // The account's row under a share lock, after the membership row ReadAgainAsync took: an account deletion locks it and then deletes
        // the logins, so a login added for an account that is going would otherwise be a foreign-key failure.
        if (!await AtomicSignInResult.AccountExistsAsync(db, current.UserId, CancellationToken.None))
        {
            return ApiResults.Error(TenancyErrors.SessionRequired);
        }

        if (!current.Session.IsConfirmedAt(now))
        {
            return ApiResults.Error(TenancyErrors.ConfirmationRequired);   // the rollback gives the ticket back: nothing was used up yet
        }

        var used = await ProviderTickets.ConsumeAsync(db, ticketText, binding, ProviderTicketPurpose.Connect, now, CancellationToken.None);
        if (used is null)
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);
        }

        // Steps 5 to 7 again: a second connect of the same identity is the 204 of step 5, of another identity of the provider the 409 of step 7.
        if (await ProviderLogins.IsLoginOfAsync(db, current.UserId, used.Provider, used.IdentityKey, CancellationToken.None))
        {
            await transaction.CommitAsync(CancellationToken.None);   // the ticket is used up, nothing else
            ProviderBindingCookie.Clear(http.Response);
            return ApiResults.NoContent();
        }

        if (await RefusalOfLoginsAsync(db, current.UserId, used, CancellationToken.None) is { } refusal)
        {
            return refusal;   // rolled back: the ticket stays
        }

        ProviderLogins.Add(db, current.UserId, used.Provider, used.IdentityKey);
        await notices.QueueAsync(db, MailKind.ProviderLinked, current.NormalizedEmail, used.Provider, now, CancellationToken.None);
        audit.Stage(SettingsCallers.Entry(
            AuditKinds.ProviderLinked, current, new Dictionary<string, object?> { ["provider"] = used.Provider, ["via"] = "settings" }));
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        ProviderBindingCookie.Clear(http.Response);
        notices.Notify();
        return ApiResults.NoContent();
    }

    /// <summary>Steps 6 and 7: the identity is another account's login, or this account has a login of the provider.</summary>
    private static async Task<IResult?> RefusalOfLoginsAsync(AuthDbContext db, Guid userId, ProviderTicket ticket, CancellationToken cancellationToken)
    {
        if (await ProviderLogins.FindAccountAsync(db, ticket.Provider, ticket.IdentityKey, cancellationToken) is { } owner && owner != userId)
        {
            return ApiResults.Error(TenancyErrors.ProviderAlreadyLinked);
        }

        return await ProviderLogins.HasLoginOfProviderAsync(db, userId, ticket.Provider, cancellationToken)
            ? ApiResults.Error(TenancyErrors.ProviderInUse)
            : null;
    }

    private static async Task<IResult> UseUpAsync(HttpContext http, AuthDbContext db, string ticketText, string binding, TimeProvider clock)
    {
        var used = await ProviderTickets.ConsumeAsync(
            db, ticketText, binding, ProviderTicketPurpose.Connect, StorableTime.Now(clock), CancellationToken.None);
        if (used is null)
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);   // used by another request meanwhile
        }

        ProviderBindingCookie.Clear(http.Response);
        return ApiResults.NoContent();
    }
}
