using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Providers;
using Auth.Server.Requests;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;

namespace Auth.Server.Account;

/// <summary>
/// <c>POST /auth/account/confirm</c> (spec 0012): the person confirms who they are on this device, with their password or, without one, a
/// <c>reauth</c> ticket of a linked identity, and the session of this device is confirmed for five minutes. The checks run in the order of the
/// contract, which mirrors <c>DELETE /auth/account</c>: the body; the session and the membership; the fit (<c>password_required</c>,
/// <c>reauth_required</c>, nothing counted); the password, counted first in the account's login streak, or the ticket, never counted; then, in one
/// transaction under the address lock where the session and the membership are read again, the window, the ticket used up and
/// <c>account.confirmed</c>.
/// </summary>
public static class AccountConfirmEndpoint
{
    public const string Path = "/auth/account/confirm";

    private const string PasswordField = "password";
    private const string TicketField = "provider_ticket";

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, LoginStreakStore streaks,
        IOpenIddictTokenManager tokens, AuditLog audit, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);

        // 1. Exactly one of the two: a password that is a non-blank string with no NUL, or a ticket that is a string (a blank one is no live ticket).
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || fields.Has(PasswordField) == fields.Has(TicketField))
        {
            return ApiResults.InvalidRequest();
        }

        string? password = null;
        string? ticketText = null;
        var readable = fields.Has(PasswordField)
            ? fields.TryString(PasswordField, out password) && !password.Contains('\0', StringComparison.Ordinal)
            : fields.TryAnyString(TicketField, out ticketText);
        if (!readable)
        {
            return ApiResults.InvalidRequest();
        }

        // 2.
        var read = await SettingsCallers.ReadAsync(http, db, tokens, memberships, clock, http.RequestAborted);
        if (read.Caller is not { } caller)
        {
            return read.Refusal!;
        }

        // 3. The confirmation must fit the account; nothing is counted and no row is written.
        if (caller.PasswordHash is not null && ticketText is not null)
        {
            return ApiResults.Error(TenancyErrors.PasswordRequired);
        }

        if (caller.PasswordHash is null && password is not null)
        {
            return ApiResults.Error(TenancyErrors.ReauthRequired);
        }

        string method;
        string? binding = null;
        if (password is not null)
        {
            // 4. As login judges it: counted first, in the streak of the account's address.
            var identifier = LoginIdentifier.HashOf(caller.NormalizedEmail);
            var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
            if (!decision.Allowed)
            {
                await SettingsCallers.RefuseAsync(audit, caller, "locked", SettingsCallers.ConfirmAction);
                return new TooManyAttemptsResult(decision.RetryAfter);
            }

            if (!SettingsCallers.PasswordIsRight(users, caller.PasswordHash!, password))
            {
                await SettingsCallers.RefuseAsync(audit, caller, "wrong_password", SettingsCallers.ConfirmAction);
                return ApiResults.Error(TenancyErrors.WrongPassword);
            }

            // Not the request's token: a client that goes away after its right password must not be left with a cooldown.
            await streaks.ClearAsync(identifier, CancellationToken.None);
            method = "password";
        }
        else
        {
            // 5. A live reauth ticket of this browser whose identity is a login of this account. Nothing is used up; the lockout is not consulted.
            ProviderBindingCookie.TryRead(http.Request, out binding);
            var ticket = await ProviderTickets.FindAsync(db, ticketText!, binding, StorableTime.Now(clock), http.RequestAborted);
            if (ticket is not { Purpose: ProviderTicketPurpose.Reauth }
                || !await ProviderLogins.IsLoginOfAsync(db, caller.UserId, ticket.Provider, ticket.IdentityKey, http.RequestAborted))
            {
                await SettingsCallers.RefuseAsync(audit, caller, "reauth_failed", SettingsCallers.ConfirmAction);
                return ApiResults.Error(TenancyErrors.ReauthFailed);
            }

            method = ticket.Provider;
        }

        // 6. Not the request's token from here on: a client that goes away must not leave the window half opened.
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await AddressLock.AcquireAsync(db, caller.NormalizedEmail, CancellationToken.None);
        var now = StorableTime.Now(clock);   // after the lock: a ticket that ran out while this request waited is refused
        var again = await SettingsCallers.ReadAgainAsync(db, memberships, caller, now);
        if (again.Caller is not { } current)
        {
            return again.Refusal!;
        }

        // 3, again: the fit of the confirmation to the password that the account has now. A reset or a change of the hash ends the session first, which
        // the re-read above answers; this keeps the rule of the spec literal, and a refusal here writes nothing and uses nothing up.
        if (current.PasswordHash is not null && ticketText is not null)
        {
            return ApiResults.Error(TenancyErrors.PasswordRequired);
        }

        if (current.PasswordHash is null && password is not null)
        {
            return ApiResults.Error(TenancyErrors.ReauthRequired);
        }

        if (ticketText is not null)
        {
            var used = await ProviderTickets.ConsumeAsync(db, ticketText, binding!, ProviderTicketPurpose.Reauth, now, CancellationToken.None);
            if (used is null || !await ProviderLogins.IsLoginOfAsync(db, current.UserId, used.Provider, used.IdentityKey, CancellationToken.None))
            {
                await transaction.DisposeAsync();
                await SettingsCallers.RefuseAsync(audit, current, "reauth_failed", SettingsCallers.ConfirmAction);
                return ApiResults.Error(TenancyErrors.ReauthFailed);
            }
        }

        await DeviceSessions.ConfirmAsync(db, current.Session, now + ConfirmationWindow.Lifetime, CancellationToken.None);
        audit.Stage(SettingsCallers.Entry(AuditKinds.AccountConfirmed, current, new Dictionary<string, object?> { ["method"] = method }));
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        if (ticketText is not null)
        {
            ProviderBindingCookie.Clear(http.Response);
        }

        return ApiResults.NoContent();
    }
}
