using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Providers;
using Auth.Server.Requests;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;

namespace Auth.Server.Api;

/// <summary>
/// <c>GET /auth/me</c>, <c>GET /auth/org</c>, <c>PATCH /auth/org</c> (spec 0005 → Company API) and <c>DELETE /auth/org</c>
/// (spec 0008): who the caller is, which company, its name, and its deletion. Every answer is read from the database, never
/// from the token.
/// </summary>
public static class OrgEndpoints
{
    public const string MePath = "/auth/me";
    public const string OrgPath = "/auth/org";

    private static readonly string[] RenameFields = ["name"];
    private const string NameField = "name";
    private const string PasswordField = "password";
    private const string TicketField = "provider_ticket";

    public static async Task<IResult> MeAsync(
        HttpContext http, MembershipReader memberships, UserManager<ApplicationUser> users, AuthDbContext db, IOpenIddictTokenManager tokens,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        var access = await CompanyAccess.AuthorizeAnyKindAsync(http, memberships);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        var user = await users.FindByIdAsync(caller.UserId.ToString());
        if (user?.Email is null)
        {
            return ApiResults.Error(TenancyErrors.Forbidden);
        }

        // Spec 0011: whether the account has a password, and the providers it signs in with, sorted ordinally.
        var linked = (await users.GetLoginsAsync(user)).Select(l => l.LoginProvider).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

        // Spec 0012: the window of the session of this device, read from the refresh cookie, never redeemed; no session, no window.
        var now = StorableTime.Now(clock);
        var session = await DeviceSessions.FindAsync(http, db, tokens, caller.UserId, now, http.RequestAborted);
        var until = session is not null && session.IsConfirmedAt(now) ? ConfirmationWindow.Format(session.ConfirmedUntil!.Value) : null;
        return ApiResults.Ok(new MeResponse(
            caller.UserId, user.Email, caller.CompanyId, caller.CompanyName, SpaceKinds.NameOf(caller.Kind), [caller.RoleName], [.. caller.Permissions],
            user.PasswordHash is not null, [.. linked], until));
    }

    public static async Task<IResult> GetAsync(HttpContext http, MembershipReader memberships)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships);
        return access.Caller is { } caller
            ? ApiResults.Ok(new OrgResponse(caller.CompanyId, caller.CompanyName))
            : access.Failure!;
    }

    public static async Task<IResult> RenameAsync(HttpContext http, MembershipReader memberships, CompanyService companies)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(companies);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.OrgManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, RenameFields, http.RequestAborted);
        if (fields is null || !NameInput.IsValid(fields[0]))
        {
            return ApiResults.InvalidRequest();
        }

        // Under the company lock, with the caller read again: they may have lost org:manage since the check above.
        var outcome = await companies.RenameAsync(Actor.Of(caller), caller.CompanyId, fields[0], http.RequestAborted);
        return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
    }

    /// <summary>
    /// <c>DELETE /auth/org</c> (spec 0008 → Company deletion). The checks run in this order: the permission <c>org:delete</c> (read from
    /// the database), the body (spec 0012: the name and exactly one of a password and a provider ticket), the fit of the confirmation to the
    /// account (<c>403 password_required</c> for a ticket of an account with a password, <c>403 reauth_required</c> for a password of an account
    /// without one: spec 0011, Amendment A9; spec 0012; nothing is counted, no row is written, nothing is deleted, as <c>DELETE /auth/account</c>
    /// judges it), then for a password the lockout of the caller's identifier and the password, and for a ticket the ticket (it must be a live
    /// <c>reauth</c> ticket of this browser whose identity is a login of the caller; the lockout is not consulted), then the company's name and
    /// safety rule 1. The password attempt is counted first, as login counts it, and a correct password ends the streak: the password is
    /// evaluated once. A ticket is used up inside the deletion's transaction, after the company lock, with the clock read there, and
    /// <c>provider.reauthenticated</c> is written with <c>org.deleted</c>; a deletion refused after that rolls the ticket back (Amendment A12).
    /// </summary>
    public static async Task<IResult> DeleteAsync(
        HttpContext http, AuthDbContext db, MembershipReader memberships, UserManager<ApplicationUser> users, LoginStreakStore streaks,
        CompanyDeletionService deletion, AuditLog audit, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(deletion);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.OrgDelete);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        // The name, and exactly one confirmation (spec 0012): a password that is a non-blank string with no NUL, or a provider ticket that is a
        // string (a blank one is no live ticket: reauth_failed).
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || !fields.TryString(NameField, out var name) || fields.Has(PasswordField) == fields.Has(TicketField))
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

        var user = await users.FindByIdAsync(caller.UserId.ToString());
        if (user?.Email is null)
        {
            return ApiResults.Error(TenancyErrors.Forbidden);
        }

        // The confirmation must fit the account, before anything is counted and with no row written (spec 0011, Amendment A9; spec 0012): the
        // guides send an admin made through a provider, who cannot give a password, to a provider confirmation.
        if (user.PasswordHash is not null && ticketText is not null)
        {
            return ApiResults.Error(TenancyErrors.PasswordRequired);
        }

        if (user.PasswordHash is null && password is not null)
        {
            return ApiResults.Error(TenancyErrors.ReauthRequired);
        }

        Func<CancellationToken, Task<string?>>? confirmInside = null;
        if (password is not null)
        {
            var identifier = LoginIdentifier.HashOf(users.NormalizeEmail(user.Email));
            var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
            if (!decision.Allowed)
            {
                await RecordRefusalAsync(audit, caller, "locked");
                return new TooManyAttemptsResult(decision.RetryAfter);
            }

            // The hash is only verified: CheckPasswordAsync would also store a new hash when the old one is out of date, an update of a row
            // this request is about to delete.
            if (!SettingsCallers.PasswordIsRight(users, user.PasswordHash!, password))
            {
                await RecordRefusalAsync(audit, caller, "wrong_password");
                return ApiResults.Error(TenancyErrors.WrongPassword);
            }

            // Not the request's token: from the tenth attempt on, counting has already started a cooldown, and a client that goes away
            // right after its correct password was verified must not be left with it (as login does).
            await streaks.ClearAsync(identifier, CancellationToken.None);
        }
        else
        {
            // A live reauth ticket of this browser whose identity is a login of the caller; nothing is used up here, the lockout is not consulted.
            ProviderBindingCookie.TryRead(http.Request, out var binding);
            var ticket = await ProviderTickets.FindAsync(db, ticketText!, binding, StorableTime.Now(clock), http.RequestAborted);
            if (ticket is not { Purpose: ProviderTicketPurpose.Reauth }
                || !await ProviderLogins.IsLoginOfAsync(db, user.Id, ticket.Provider, ticket.IdentityKey, http.RequestAborted))
            {
                await RecordRefusalAsync(audit, caller, "reauth_failed");
                return ApiResults.Error(TenancyErrors.ReauthFailed);
            }

            // Amendment A12: used up inside the deletion's transaction, after the company lock, with the clock read there.
            confirmInside = async cancellationToken =>
            {
                var used = await ProviderTickets.ConsumeAsync(
                    db, ticketText!, binding!, ProviderTicketPurpose.Reauth, StorableTime.Now(clock), cancellationToken);
                if (used is null || !await ProviderLogins.IsLoginOfAsync(db, user.Id, used.Provider, used.IdentityKey, cancellationToken))
                {
                    return TenancyErrors.ReauthFailed;
                }

                // Staged before the name check and safety rule 1 have judged the deletion: a refusal rolls the transaction back and the row stays
                // on this request's context unsaved. Nothing may save on this context after a refused deletion (the refusal rows are written on
                // contexts of their own), or this row would be written for a deletion that did not happen.
                audit.Stage(new AuditEntry
                {
                    Kind = AuditKinds.ProviderReauthenticated,
                    ActorUserId = user.Id,
                    SubjectUserId = user.Id,
                    SubjectEmail = user.Email,
                    OrgId = caller.CompanyId,
                    OrgName = caller.CompanyName,
                    Details = new Dictionary<string, object?> { ["provider"] = used.Provider },
                });
                return null;
            };
        }

        var outcome = await deletion.DeleteAsync(Actor.Of(caller), caller.CompanyId, name, http.RequestAborted, confirmInside);
        if (outcome.Succeeded)
        {
            if (ticketText is not null)
            {
                ProviderBindingCookie.Clear(http.Response);
            }

            return ApiResults.NoContent();
        }

        if (outcome.Error == TenancyErrors.ReauthFailed)
        {
            await RecordRefusalAsync(audit, caller, "reauth_failed");   // the deletion was rolled back; the row is written alone
            return ApiResults.Error(TenancyErrors.ReauthFailed);
        }

        // A company that has vanished while this request waited for its lock is a membership that is gone: the token no longer
        // matches the database, which is what the contract says for that case.
        return outcome.Error == TenancyErrors.NotFound ? ApiResults.Error(TenancyErrors.PermissionsChanged) : ApiResults.Refused(outcome);
    }

    private static Task RecordRefusalAsync(AuditLog audit, TenantContext caller, string reason) =>
        audit.WriteAloneAsync(
            new AuditEntry
            {
                Kind = AuditKinds.OrgDeleteRefused,
                OrgId = caller.CompanyId,
                OrgName = caller.CompanyName,
                Details = new Dictionary<string, object?> { ["reason"] = reason },
            },
            Actor.Of(caller));
}
