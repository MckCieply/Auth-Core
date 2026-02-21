using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Account;

/// <summary>
/// <c>POST /auth/password/reset</c>: exchanges a reset token for a new password. Everything happens in one
/// transaction that begins by using the token up: a second request with the same token waits for this one and then
/// finds no token, and a request that fails on the way — a weak password, an error — rolls back, token included.
/// On success every session of the account ends, its login streak is cleared and its email is confirmed
/// (spec 0004 → Effects of a reset). The user is not signed in.
/// </summary>
public static class ResetPasswordEndpoint
{
    public const string Path = "/auth/password/reset";

    private static readonly string[] Fields = ["token", "new_password"];

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users,
        IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, TimeProvider clock, AuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(authorizations);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);

        var cancellationToken = http.RequestAborted;
        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, cancellationToken);
        if (fields is null || fields[1].Contains('\0'))
        {
            return AccountResults.InvalidRequest();
        }

        var (token, password) = (fields[0], fields[1]);
        var now = StorableTime.Now(clock);

        // Leaving this method without a commit rolls everything back, the use of the token included.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await EmailTokens.ConsumeAsync(db, token, MailKind.PasswordReset, now, cancellationToken) is not { } userId
            || await users.FindByIdAsync(userId.ToString()) is not { } user)
        {
            return AccountResults.InvalidToken();
        }

        var broken = await PasswordRules.BrokenAsync(users, user, password);
        if (broken.Count > 0)
        {
            return AccountResults.WeakPassword(broken);
        }

        // The link came through the account's mailbox: that proves the address as well.
        user.EmailConfirmed = true;
        if (await users.HasPasswordAsync(user))
        {
            Ensure(await users.RemovePasswordAsync(user));
        }

        Ensure(await users.AddPasswordAsync(user, password));

        // Every session ends, every other link goes, the streak is cleared (spec 0004 → Effects of a reset).
        await AccountSessions.EndAllAsync(db, tokens, authorizations, user, cancellationToken);

        // The row is written by the save inside this transaction: the reset and its row, both or neither. Who, never the link or the password.
        audit.Stage(new AuditEntry { Kind = AuditKinds.PasswordReset, SubjectUserId = user.Id, SubjectEmail = user.Email });
        await db.SaveChangesAsync(CancellationToken.None);

        // Not the request's token: a client that goes away now must not leave the outcome open.
        await transaction.CommitAsync(CancellationToken.None);
        return AccountResults.Done();
    }

    private static void Ensure(IdentityResult result) => AccountSessions.Ensure(result, "Could not set the password");
}
