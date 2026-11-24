using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Sessions;

/// <summary>How a sign-in uses the membership of the session's space: it takes it (writes <c>LastUsedAt</c>) or only holds it.</summary>
public enum SpaceUse
{
    /// <summary>A login, a provider sign-in, a completed sign-up or a switch: the membership is locked <c>FOR NO KEY UPDATE</c> and <c>LastUsedAt</c> is written.</summary>
    Take,

    /// <summary>A refresh: the membership is locked <c>FOR KEY SHARE</c> and nothing is written (a refresh never writes <c>LastUsedAt</c>).</summary>
    Hold,
}

/// <summary>
/// The row locks a sign-in takes on the membership of the session's space (spec 0015 → Lock order), in the transaction that is open on the
/// context. A removal of the member deletes the row, which conflicts with both modes, so a session is never written for a membership that is
/// being removed and one that is already gone gets none. The membership is the one the session names: the same <c>JoinedAt</c> in whole
/// microseconds, so a membership removed and made again is not it. The time is compared on the value read from the database, never by a SQL
/// equality on a parameter (a parameter would be rounded and bound differently).
/// </summary>
public static class SpaceLocks
{
    /// <summary>
    /// Locks the row <c>FOR NO KEY UPDATE</c> and sets <c>LastUsedAt</c> to <paramref name="now"/>, in the open transaction. <see langword="false"/>
    /// when the row is gone or its <c>JoinedAt</c> is not the session's (nothing is written then).
    /// </summary>
    public static async Task<bool> TakeAsync(AuthDbContext db, Guid userId, SessionSpace space, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var joined = await db.Database
            .SqlQuery<DateTimeOffset>($"""SELECT "JoinedAt" AS "Value" FROM "Memberships" WHERE "UserId" = {userId} AND "CompanyId" = {space.CompanyId} FOR NO KEY UPDATE""")
            .ToListAsync(cancellationToken);
        if (joined.Count != 1 || SessionCompany.MicrosOf(joined[0]) != space.JoinedMicros)
        {
            return false;
        }

        // The row is locked by this transaction: the update waits for nothing, and it commits or rolls back with the session.
        await db.Memberships
            .Where(m => m.UserId == userId && m.CompanyId == space.CompanyId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedAt, now), cancellationToken);
        return true;
    }

    /// <summary>
    /// Locks the user's membership in a company <c>FOR NO KEY UPDATE</c>, the mode a sign-in takes it in, and writes nothing (spec 0012 → Lock
    /// order: a settings change and a provider sign-in take it before the account's row, as spec 0015's order says; the session it renews then
    /// takes the same lock again). <see langword="false"/> when the row is gone.
    /// </summary>
    public static async Task<bool> LockRowAsync(AuthDbContext db, Guid userId, Guid companyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var found = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "Memberships" WHERE "UserId" = {userId} AND "CompanyId" = {companyId} FOR NO KEY UPDATE""")
            .ToListAsync(cancellationToken);
        return found.Count == 1;
    }

    /// <summary>Locks the row <c>FOR KEY SHARE</c> and writes nothing. <see langword="false"/> in the cases of <see cref="TakeAsync"/>.</summary>
    public static async Task<bool> HoldAsync(AuthDbContext db, Guid userId, SessionSpace space, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var joined = await db.Database
            .SqlQuery<DateTimeOffset>($"""SELECT "JoinedAt" AS "Value" FROM "Memberships" WHERE "UserId" = {userId} AND "CompanyId" = {space.CompanyId} FOR KEY SHARE""")
            .ToListAsync(cancellationToken);
        return joined.Count == 1 && SessionCompany.MicrosOf(joined[0]) == space.JoinedMicros;
    }
}
