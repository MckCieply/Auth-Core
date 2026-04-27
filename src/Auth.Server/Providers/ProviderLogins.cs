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
}
