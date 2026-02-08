using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// Applies the mail limit to one identifier and kind inside the caller's transaction: one statement creates the limit
/// row or locks the existing one (the pattern of <c>LoginStreakStore</c>), the rules decide, and an accepted request
/// changes the tracked row. The caller saves, so the limit and what it lets through commit together. Shared by the
/// queue of spec 0004 (an identifier per address) and the invitations of spec 0005 (one per company and address).
/// </summary>
public static class MailLimits
{
    public static async Task<MailLimitDecision> RegisterAsync(
        AuthDbContext db, byte[] identifierHash, MailKind kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(identifierHash);

        var kindValue = (short)kind;

        // ToListAsync, not SingleAsync: EF must send the statement as it is.
        var rows = await db.MailRequestLimits
            .FromSql($"""
                INSERT INTO "MailRequestLimits" ("IdentifierHash", "Kind", "WindowStartedAt", "WindowCount", "LastAcceptedAt")
                VALUES ({identifierHash}, {kindValue}, {now}, 0, {now})
                ON CONFLICT ("IdentifierHash", "Kind") DO UPDATE SET "WindowCount" = "MailRequestLimits"."WindowCount"
                RETURNING *
                """)
            .ToListAsync(cancellationToken);
        var row = rows.Single();

        var (next, decision) = MailLimitPolicy.Register(
            new MailLimitState(row.WindowStartedAt, row.WindowCount, row.LastAcceptedAt), now);
        if (decision.Allowed)
        {
            row.WindowStartedAt = next.WindowStartedAt;
            row.WindowCount = next.WindowCount;
            row.LastAcceptedAt = next.LastAcceptedAt;
        }

        return decision;
    }
}
