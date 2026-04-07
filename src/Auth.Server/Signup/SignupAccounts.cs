using Auth.Infrastructure.Persistence;
using Auth.Server.Requests;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Signup;

/// <summary>
/// The account under the address of a sign-up, as far as a sign-up cares (spec 0009, Amendment 3): its id, its address as it stores it, and
/// whether it belongs to a company.
/// </summary>
public sealed record SignupAccount(Guid Id, string Email, bool BelongsToCompany)
{
    /// <summary>
    /// Whether a sign-up link sent to <paramref name="typedEmail"/> may start a company for this account: it belongs to none, and its address is
    /// the typed one up to ASCII case. The account lookup folds some different addresses into one (the micro sign and Greek mu, final sigma and
    /// sigma…); a link proves only the mailbox it was sent to, so an account that merely looks like the typed address is never used
    /// (<see cref="EmailInput.IsSameAddress"/>, the rule invitation acceptance applies).
    /// </summary>
    public bool MayStartCompany(string typedEmail)
    {
        ArgumentNullException.ThrowIfNull(typedEmail);

        return !BelongsToCompany && EmailInput.IsSameAddress(Email, typedEmail);
    }
}

public static class SignupAccounts
{
    /// <summary>The first account (by id) under the normalised address; <see langword="null"/> when there is none.</summary>
    public static async Task<SignupAccount?> FindAsync(AuthDbContext db, string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        var row = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.Email, BelongsToCompany = db.Memberships.Any(m => m.UserId == u.Id) })
            .FirstOrDefaultAsync(cancellationToken);
        return row is { Email: { } email } ? new SignupAccount(row.Id, email, row.BelongsToCompany) : null;
    }
}
