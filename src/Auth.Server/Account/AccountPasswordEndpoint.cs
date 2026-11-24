using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Login;
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
/// <c>POST /auth/account/password</c> (spec 0012): changes the password of an account that has one, against the current password, every time
/// (the window plays no part). The checks run in the order of the contract: the body; the session and the membership; an account without a
/// password (<c>403 password_not_set</c>); the policy (<c>400 weak_password</c>); the current password, counted first in the login streak; then,
/// in one transaction under the address lock, in the order of Amendment A2: the session, the membership and the password's presence read again;
/// the account's row under a key-share lock; the account's email links deleted; its hash read under a share lock, which must be the one that was
/// judged (a reset committed meanwhile: <c>403 wrong_password</c>, not counted again); the new hash and stamp; every session ended;
/// <c>password.changed</c>, the notice "password changed" and a renewed session for this device, answered as a login. The login streak is cleared
/// where the right password is recognised, on the connection of its own that <see cref="LoginStreakStore"/> uses: it is never written on the
/// request's context inside this transaction. A reset that committed before this transaction took the address lock ended every session, this
/// one's too: <c>401 session_required</c>.
/// </summary>
public static class AccountPasswordEndpoint
{
    private const string CurrentField = "current_password";
    private const string NewField = "new_password";

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, LoginStreakStore streaks,
        IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, AuditLog audit, MailNotices notices,
        IOptions<TokenOptions> tokenOptions, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(authorizations);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(tokenOptions);
        ArgumentNullException.ThrowIfNull(clock);

        // Without it OpenIddict cannot sign in here: fail at once, as the unlink does.
        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException("The password change was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

        // 1. The new password a string with no NUL (a blank one is judged by the policy), the current one a non-blank string with no NUL.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields
            || !fields.TryAnyString(NewField, out var next)
            || next.Contains('\0', StringComparison.Ordinal)
            || !fields.TryString(CurrentField, out var current)
            || current.Contains('\0', StringComparison.Ordinal))
        {
            return ApiResults.InvalidRequest();
        }

        // 2.
        var read = await SettingsCallers.ReadAsync(http, db, tokens, memberships, clock, http.RequestAborted);
        if (read.Caller is not { } caller)
        {
            return read.Refusal!;
        }

        // 3. A first password goes through the mailbox (Decision 17); nothing is counted and nothing is written.
        if (caller.PasswordHash is not { } judged)
        {
            return ApiResults.Error(TenancyErrors.PasswordNotSet);
        }

        // 4. The policy looks at the password alone; the account the change updates is read afresh under the lock.
        var broken = await PasswordRules.BrokenAsync(users, new ApplicationUser(), next);
        if (broken.Count > 0)
        {
            return AccountResults.WeakPassword(broken);
        }

        // 5. As POST /auth/account/confirm judges a password: counted first.
        var identifier = LoginIdentifier.HashOf(caller.NormalizedEmail);
        var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
        if (!decision.Allowed)
        {
            await SettingsCallers.RefuseAsync(audit, caller, "locked", SettingsCallers.PasswordAction);
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        if (!SettingsCallers.PasswordIsRight(users, judged, current))
        {
            await SettingsCallers.RefuseAsync(audit, caller, "wrong_password", SettingsCallers.PasswordAction);
            return ApiResults.Error(TenancyErrors.WrongPassword);
        }

        // Not the request's token: a client that goes away after its right password must not be left with a cooldown.
        await streaks.ClearAsync(identifier, CancellationToken.None);

        // 6. Not the request's token from here on: a client that goes away must not leave the change half done.
        var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        try
        {
            await AddressLock.AcquireAsync(db, caller.NormalizedEmail, CancellationToken.None);
            var now = StorableTime.Now(clock);
            var again = await SettingsCallers.ReadAgainAsync(db, memberships, caller, now);
            if (again.Caller is not { } owner)
            {
                await transaction.DisposeAsync();
                return again.Refusal!;   // a second change at once, or a reset that committed first, finds the session ended
            }

            if (owner.PasswordHash is null || !SessionRenewal.CanRenew(owner.Session, now))
            {
                await transaction.DisposeAsync();
                return ApiResults.Error(owner.PasswordHash is null ? TenancyErrors.PasswordNotSet : TenancyErrors.SessionRequired);
            }

            // The account's row under a key-share lock to the end, before any row is deleted or revoked: an account deletion (a member removal,
            // a company deletion: they take no address lock) locks that row for update and then deletes the email links and the tokens, so
            // reaching the row after the links would wait for a deletion that waits for them. It does not conflict with a sign-in, a refresh or a
            // reset (which updates the row without touching its key).
            if (!await AtomicSignInResult.AccountExistsAsync(db, owner.UserId, CancellationToken.None))
            {
                await transaction.DisposeAsync();
                return ApiResults.Error(TenancyErrors.SessionRequired);
            }

            // Amendment A2: the links first, so that a reset meets this change at its link row (it holds the row it used up from its first
            // statement) and the first one wins. The share lock on the hash is asked for only after them. The reset's own kind goes first, in
            // a statement of its own: a reset that holds that link then makes this change wait while it holds nothing, so that the reset's
            // delete of the account's other links (which it makes at its end) never waits for a link that this change has already deleted.
            await db.EmailTokens.Where(t => t.UserId == owner.UserId && t.Kind == MailKind.PasswordReset).ExecuteDeleteAsync(CancellationToken.None);
            await db.EmailTokens.Where(t => t.UserId == owner.UserId).ExecuteDeleteAsync(CancellationToken.None);
            var hashes = await db.Database
                .SqlQuery<string?>($"""SELECT "PasswordHash" AS "Value" FROM "AspNetUsers" WHERE "Id" = {owner.UserId} FOR SHARE""")
                .ToListAsync(CancellationToken.None);
            if (hashes is not [{ } held] || !string.Equals(held, judged, StringComparison.Ordinal))
            {
                await transaction.DisposeAsync();
                await SettingsCallers.RefuseAsync(audit, owner, "wrong_password", SettingsCallers.PasswordAction);   // not counted again
                return ApiResults.Error(TenancyErrors.WrongPassword);
            }

            var account = await users.FindByIdAsync(owner.UserId.ToString())
                ?? throw new InvalidOperationException("The account held under a share lock is gone.");
            Ensure(await users.RemovePasswordAsync(account));
            Ensure(await users.AddPasswordAsync(account, next));   // the security stamp changes, as at a reset

            await SessionRenewal.EndSessionsAsync(tokens, authorizations, owner.UserId, CancellationToken.None);
            await notices.QueueAsync(db, MailKind.PasswordChanged, owner.NormalizedEmail, null, now, CancellationToken.None);
            audit.Stage(SettingsCallers.Entry(AuditKinds.PasswordChanged, owner));
            return SessionRenewal.Issue(http, account, owner, transaction, tokenOptions.Value.Audience, clock, _ => notices.Notify());
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private static void Ensure(IdentityResult result) => AccountSessions.Ensure(result, "Could not set the password");
}
