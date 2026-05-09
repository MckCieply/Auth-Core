using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Providers;

/// <summary>
/// The lock of one provider identity: a transaction-scoped advisory lock on the provider's name and the identity key. Every flow that makes an
/// identity a login of an account (the link, the finish, the acceptance of an invitation through a provider, the connect) takes the address lock
/// of its account first, so two of them for one address never run at once; but the address in a ticket is only what the provider said at that round
/// trip, so two accounts with different addresses can each hold a ticket of one identity, and their address locks do not exclude each other.
/// This lock does: the later of the two waits here, reads the identity's login again and answers as an identity that is linked already (never a
/// failure of the primary key of the logins). Order: always the address lock (after the company lock, where a flow takes one), then this lock,
/// and nothing that waits for an address or a company lock after it, so the locks never wait for each other in a cycle.
/// </summary>
public static class IdentityLock
{
    // The key has no '@': it cannot be the text of an address lock, and the other seed keeps the two hash spaces apart as well.
    private const long Seed = 1;

    /// <summary>Takes the lock until the transaction the caller has opened ends.</summary>
    public static Task AcquireAsync(AuthDbContext db, string provider, string identityKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(identityKey);

        var key = $"login:{provider}:{identityKey}";
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, {Seed}))", cancellationToken);
    }
}
