using Auth.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Providers;

/// <summary>
/// The identities of spec 0011 are Identity's logins (<c>AspNetUserLogins</c>, Decision 2): <c>LoginProvider</c> is the provider's name,
/// <c>ProviderKey</c> the identity key. The primary key of the table is the two together, so an identity is the login of one account at most; an
/// account deletion removes its logins by cascade (spec 0010).
/// </summary>
public static class ProviderLogins
{
    /// <summary>The account the identity is a login of; <see langword="null"/> when it is none's.</summary>
    public static Task<Guid?> FindAccountAsync(AuthDbContext db, string provider, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.UserLogins.AsNoTracking()
            .Where(l => l.LoginProvider == provider && l.ProviderKey == key)
            .Select(l => (Guid?)l.UserId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Adds the identity as a login of the account to the context; the caller saves, inside its transaction.</summary>
    public static void Add(AuthDbContext db, Guid userId, string provider, string key)
    {
        ArgumentNullException.ThrowIfNull(db);

        db.UserLogins.Add(new IdentityUserLogin<Guid>
        {
            LoginProvider = provider,
            ProviderKey = key,
            ProviderDisplayName = ProviderNames.DisplayNameOf(provider),
            UserId = userId,
        });
    }

    public static Task<bool> IsLoginOfAsync(AuthDbContext db, Guid userId, string provider, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.UserLogins.AnyAsync(l => l.UserId == userId && l.LoginProvider == provider && l.ProviderKey == key, cancellationToken);
    }

    /// <summary>
    /// Reads the identity's login row of the account under a share lock held to the end of the caller's transaction (spec 0012 → Sign-in of a
    /// linked identity): an unlink that comes second waits for the sign-in and then ends its session. <see langword="false"/> when the row is gone
    /// (an unlink committed first) or the account is (a deletion committed first). The account's row is read first, <c>FOR KEY SHARE</c>, as the
    /// sign-in's own commit does: an account deletion locks that row and then deletes the login rows, so a sign-in that held a login row and then
    /// asked for the account's row would wait for the deletion while the deletion waited for it (a cycle); an unlink takes no stronger lock on the
    /// account's row, so the order costs it nothing.
    /// </summary>
    public static async Task<bool> HoldAsync(AuthDbContext db, Guid userId, string provider, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var accounts = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "AspNetUsers" WHERE "Id" = {userId} FOR KEY SHARE""")
            .ToListAsync(cancellationToken);
        if (accounts.Count != 1)
        {
            return false;
        }

        var rows = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "AspNetUserLogins" AS l WHERE l."UserId" = {userId} AND l."LoginProvider" = {provider} AND l."ProviderKey" = {key} FOR SHARE""")
            .ToListAsync(cancellationToken);
        return rows.Count == 1;
    }

    /// <summary>Whether the account has a login of <paramref name="provider"/> (spec 0012: one login per provider per account).</summary>
    public static Task<bool> HasLoginOfProviderAsync(AuthDbContext db, Guid userId, string provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.UserLogins.AsNoTracking().AnyAsync(l => l.UserId == userId && l.LoginProvider == provider, cancellationToken);
    }
}
