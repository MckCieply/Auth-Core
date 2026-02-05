using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// The lock that serialises everything that changes one company's members, roles or invitations (spec 0005 → Safety
/// rules, rule 2): a row lock on the company, taken first, in a transaction left at the default isolation level. A
/// higher level would not do: under <c>REPEATABLE READ</c> the later transaction keeps reading the snapshot it took
/// before the lock was granted, and the check it makes is made against rows that have since changed.
/// </summary>
public static class CompanyLock
{
    /// <summary>Locks the company's row until the transaction ends; <see langword="false"/> when there is no such company.</summary>
    public static async Task<bool> AcquireAsync(AuthDbContext db, Guid companyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var locked = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "Companies" WHERE "Id" = {companyId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return locked.Count == 1;
    }
}
