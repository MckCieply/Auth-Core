using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Login;
using Auth.Server.Providers;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Account;

/// <summary>
/// <c>POST /auth/account/logins/unlink</c> (spec 0012): the account loses its logins of one provider, inside the confirmation window of this device,
/// never its last way in (the password, or a login of an offered provider). The checks run in the order of the contract: the body; the session
/// and the membership; the window; a provider name the account has a login of (<c>404 not_found</c>); a way in left (<c>409 last_login</c>);
/// then, in one transaction under the address lock where the session, the membership, the window and steps 4 and 5 are read again and the cap
/// of the session is checked, the account's row read under a share lock, the login rows deleted (waiting for a sign-in that holds them), every session ended, <c>provider.unlinked</c>, the notice
/// "provider disconnected" and a renewed session for this device, answered as a login.
/// </summary>
public static class LoginUnlinkEndpoint
{
    private const string ProviderField = "provider";

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, IOpenIddictTokenManager tokens,
        IOpenIddictAuthorizationManager authorizations, ProviderCatalog catalog, AuditLog audit, MailNotices notices, IOptions<TokenOptions> tokenOptions,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(authorizations);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(tokenOptions);
        ArgumentNullException.ThrowIfNull(clock);

        // Without it OpenIddict cannot sign in here: fail at once, as the exchange does.
        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException("The unlink was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

        // 1.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || !fields.TryAnyString(ProviderField, out var provider))
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
        if (!caller.Session.IsConfirmedAt(StorableTime.Now(clock)))
        {
            return ApiResults.Error(TenancyErrors.ConfirmationRequired);
        }

        // 4 and 5.
        if (await RefusalAsync(db, catalog, caller, provider, http.RequestAborted) is { } refusal)
        {
            return refusal;
        }

        // 6. Not the request's token from here on: a client that goes away must not leave the unlink half done.
        var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        try
        {
            await AddressLock.AcquireAsync(db, caller.NormalizedEmail, CancellationToken.None);
            var now = StorableTime.Now(clock);   // after the lock: a window that ended while this request waited is refused
            var again = await SettingsCallers.ReadAgainAsync(db, memberships, caller, now);
            if (again.Caller is not { } current)
            {
                await transaction.DisposeAsync();
                return again.Refusal!;   // a second unlink at once finds its session ended by the first
            }

            if (!current.Session.IsConfirmedAt(now))
            {
                await transaction.DisposeAsync();
                return ApiResults.Error(TenancyErrors.ConfirmationRequired);   // the window ended while this request waited for the lock
            }

            var late = await RefusalAsync(db, catalog, current, provider, CancellationToken.None);
            var account = late is null && SessionRenewal.CanRenew(current.Session, now) ? await users.FindByIdAsync(current.UserId.ToString()) : null;
            if (late is not null || account is null)
            {
                await transaction.DisposeAsync();
                return late ?? ApiResults.Error(TenancyErrors.SessionRequired);
            }

            // The account's row under a share lock to the end, before any row is deleted or revoked: an account deletion (a member removal, a company
            // deletion: they take no address lock) locks that row for update and then deletes the logins and tokens, so taking it after them would
            // wait for a deletion that waits for these rows. It does not conflict with a sign-in, which holds the same lock, nor with a reset.
            if (!await AtomicSignInResult.AccountExistsAsync(db, current.UserId, CancellationToken.None))
            {
                await transaction.DisposeAsync();
                return ApiResults.Error(TenancyErrors.SessionRequired);
            }

            // Waits for a sign-in that holds one of these rows (spec 0012 → Sign-in of a linked identity); that sign-in's session ends below.
            await db.UserLogins.Where(l => l.UserId == current.UserId && l.LoginProvider == provider).ExecuteDeleteAsync(CancellationToken.None);
            await SessionRenewal.EndSessionsAsync(tokens, authorizations, current.UserId, CancellationToken.None);
            await notices.QueueAsync(db, MailKind.ProviderUnlinked, current.NormalizedEmail, provider, now, CancellationToken.None);
            audit.Stage(SettingsCallers.Entry(AuditKinds.ProviderUnlinked, current, new Dictionary<string, object?> { ["provider"] = provider }));
            return SessionRenewal.Issue(http, account, current, transaction, tokenOptions.Value.Audience, clock, _ => notices.Notify());
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Steps 4 and 5: the name must be a provider's and the account must have a login of it (a login of a provider no longer offered included);
    /// afterwards the password or a login of another offered provider must remain.
    /// </summary>
    private static async Task<IResult?> RefusalAsync(
        AuthDbContext db, ProviderCatalog catalog, SettingsCaller caller, string provider, CancellationToken cancellationToken)
    {
        var providers = await db.UserLogins.AsNoTracking()
            .Where(l => l.UserId == caller.UserId)
            .Select(l => l.LoginProvider)
            .ToListAsync(cancellationToken);
        if (!ProviderNames.All.Contains(provider, StringComparer.Ordinal) || !providers.Contains(provider, StringComparer.Ordinal))
        {
            return ApiResults.NotFound();
        }

        var wayIn = caller.PasswordHash is not null
            || providers.Any(p => !string.Equals(p, provider, StringComparison.Ordinal) && catalog.IsOffered(p));
        return wayIn ? null : ApiResults.Error(TenancyErrors.LastLogin);
    }
}
