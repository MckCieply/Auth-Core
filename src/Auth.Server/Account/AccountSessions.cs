using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Lockout;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Account;

/// <summary>
/// What a new password link does to an account, in one place: a reset (spec 0004) and the acceptance of an invitation
/// (spec 0005) both end every session of the account the same way, and two copies could drift apart.
/// </summary>
internal static class AccountSessions
{
    /// <summary>
    /// Ends every session of the account: every refresh token and the authorizations they hang on (spec 0002, Decision 11),
    /// every other email link of the account, of either kind, and its login streak — someone failing logins on purpose
    /// must not keep the owner out. Needs the account's <c>NormalizedEmail</c>, which is the key of the streak.
    /// </summary>
    public static async Task EndAllAsync(
        AuthDbContext db, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations,
        ApplicationUser user, CancellationToken cancellationToken)
    {
        var subject = user.Id.ToString();
        await tokens.RevokeBySubjectAsync(subject, cancellationToken);
        await authorizations.RevokeBySubjectAsync(subject, cancellationToken);
        await db.EmailTokens.Where(t => t.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);

        var identifier = LoginIdentifier.HashOf(user.NormalizedEmail);
        await db.LoginStreaks.Where(s => s.IdentifierHash == identifier).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Throws when Identity refused; the message carries error codes only.</summary>
    public static void Ensure(IdentityResult result, string message)
    {
        if (!result.Succeeded)
        {
            // Codes only: descriptions can echo policy details, and a password must never be logged.
            throw new InvalidOperationException(message + ": " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }
    }
}
