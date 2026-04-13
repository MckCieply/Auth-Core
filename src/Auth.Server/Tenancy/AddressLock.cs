using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// The lock of one address (specs 0009 and 0010 → Locks): a transaction-scoped advisory lock on the normalised address, the lock that sign-up
/// completion and invitation acceptance take. Spec 0010 takes it where no company lock guards the account: self-deletion, the move of a private
/// account into a company, and the start-up step. A flow that also changes a company takes the company lock first.
/// </summary>
public static class AddressLock
{
    /// <summary>Takes the lock until the transaction the caller has opened ends.</summary>
    public static Task AcquireAsync(AuthDbContext db, string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({normalizedEmail}, 0))", cancellationToken);
    }
}
