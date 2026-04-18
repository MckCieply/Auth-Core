using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Signup;

/// <summary>
/// The account under the address of a sign-up (spec 0010 → Mails and Complete): any account, of either kind, under any spelling the normaliser
/// folds together; its id and its address as it stores it (the "you already have an account" mail goes to that address).
/// </summary>
public sealed record SignupAccount(Guid Id, string Email);

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
            .Select(u => new { u.Id, u.Email })
            .FirstOrDefaultAsync(cancellationToken);
        return row is { Email: { } email } ? new SignupAccount(row.Id, email) : null;
    }
}
