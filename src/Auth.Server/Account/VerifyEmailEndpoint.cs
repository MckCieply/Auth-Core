using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Account;

/// <summary>
/// <c>POST /auth/email/verify</c>: exchanges a verification token for a confirmed email. One transaction that begins
/// by using the token up, like the reset. It changes nothing else about the account: not the password, not its
/// sessions, not a reset link.
/// </summary>
public static class VerifyEmailEndpoint
{
    public const string Path = "/auth/email/verify";

    private static readonly string[] Fields = ["token"];

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(clock);

        var cancellationToken = http.RequestAborted;
        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, cancellationToken);
        if (fields is null)
        {
            return AccountResults.InvalidRequest();
        }

        var now = StorableTime.Now(clock);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await EmailTokens.ConsumeAsync(db, fields[0], MailKind.EmailVerification, now, cancellationToken) is not { } userId
            || await users.FindByIdAsync(userId.ToString()) is not { } user)
        {
            return AccountResults.InvalidToken();
        }

        user.EmailConfirmed = true;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not confirm the email: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }

        // Not the request's token: a client that goes away now must not leave the outcome open.
        await transaction.CommitAsync(CancellationToken.None);
        return AccountResults.Done();
    }
}
