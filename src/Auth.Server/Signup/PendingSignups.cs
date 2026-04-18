using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Signup;

/// <summary>
/// The statements of pending sign-ups (spec 0009). Each runs in the caller's context, so the caller's transaction decides whether it
/// happened. The link tokens work as those of spec 0004 and of invitations: 32 random bytes as base64url, only the SHA-256 stored,
/// single use.
/// </summary>
public static class PendingSignups
{
    /// <summary>A pending sign-up lives this long from its request, and a link from its mail. A constant, as the lifetimes of spec 0004.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// Makes the pending sign-up of the address, or replaces the one that is there (its typed address, its kind, its company name and its
    /// time); one statement, so two requests at once leave one row. The current link and its time are left as they are: until the new mail has
    /// been sent, the earlier link is still the one that works — unless the typed address differs from the stored one by more than ASCII case
    /// (the normalised key folds the micro sign into Greek mu, for one): a link that went to another mailbox must not complete a sign-up that
    /// now names this one, so then the link is dropped and the row lives its 24 hours from this request alone. A private sign-up (spec 0010)
    /// has no company name. A request of another kind than the stored one drops the link at once, as a look-alike spelling does: a link
    /// mailed for one kind never completes the other.
    /// </summary>
    public static async Task UpsertAsync(
        AuthDbContext db, string normalizedEmail, string email, string? companyName, SignupKind kind, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        ArgumentNullException.ThrowIfNull(email);

        var expiresAt = now + Lifetime;
        var kindValue = (short)kind;
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "PendingSignups" ("NormalizedEmail", "Email", "CompanyName", "Kind", "RequestedAt", "TokenHash", "TokenExpiresAt", "ExpiresAt")
            VALUES ({normalizedEmail}, {email}, {companyName}, {kindValue}, {now}, NULL, NULL, {expiresAt})
            ON CONFLICT ("NormalizedEmail") DO UPDATE SET
                "TokenHash" = CASE WHEN translate("PendingSignups"."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = translate(EXCLUDED."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')
                                        AND "PendingSignups"."Kind" = EXCLUDED."Kind"
                                   THEN "PendingSignups"."TokenHash" END,
                "TokenExpiresAt" = CASE WHEN translate("PendingSignups"."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = translate(EXCLUDED."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')
                                             AND "PendingSignups"."Kind" = EXCLUDED."Kind"
                                        THEN "PendingSignups"."TokenExpiresAt" END,
                "ExpiresAt" = CASE WHEN translate("PendingSignups"."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = translate(EXCLUDED."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')
                                        AND "PendingSignups"."Kind" = EXCLUDED."Kind"
                                   THEN GREATEST(EXCLUDED."ExpiresAt", "PendingSignups"."TokenExpiresAt")
                                   ELSE EXCLUDED."ExpiresAt" END,
                "Email" = EXCLUDED."Email",
                "CompanyName" = EXCLUDED."CompanyName",
                "Kind" = EXCLUDED."Kind",
                "RequestedAt" = EXCLUDED."RequestedAt"
            """,
            cancellationToken);
    }

    /// <summary>
    /// Gives the pending sign-up a new link token, replacing the one it had, and returns it in clear; the caller puts it into a mail and
    /// nowhere else. The 24 hours of the link start now, and the row lives at least as long. The row should be locked by the caller (the dispatcher does).
    /// </summary>
    public static async Task<string> IssueAsync(AuthDbContext db, string normalizedEmail, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        var token = EmailTokens.NewToken();
        var hash = EmailTokens.HashOf(token);
        var expiresAt = now + Lifetime;
        var updated = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE "PendingSignups"
            SET "TokenHash" = {hash}, "TokenExpiresAt" = {expiresAt}, "ExpiresAt" = GREATEST("ExpiresAt", {expiresAt})
            WHERE "NormalizedEmail" = {normalizedEmail}
            """,
            cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException("The pending sign-up to issue a link for does not exist; the caller must hold its row lock.");
        }

        return token;
    }

    /// <summary>The pending sign-up whose current link this is, if the link is still good; nothing is changed.</summary>
    public static Task<PendingSignup?> FindUsableAsync(AuthDbContext db, byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokenHash);

        return db.PendingSignups.AsNoTracking()
            .Where(p => p.TokenHash == tokenHash && p.TokenExpiresAt > now)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The pending sign-up of the address if it has not expired, with its row locked until the transaction ends: whoever completes it,
    /// or replaces its link, waits for the mail that is being sent. <see langword="null"/> when there is none.
    /// </summary>
    public static async Task<PendingSignup?> LockUsableAsync(
        AuthDbContext db, string normalizedEmail, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        // ToListAsync, not SingleOrDefaultAsync: EF must send the statement as it is, not wrapped in a subquery.
        var rows = await db.PendingSignups
            .FromSql($"""SELECT * FROM "PendingSignups" WHERE "NormalizedEmail" = {normalizedEmail} AND "ExpiresAt" > {now} FOR UPDATE""")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    /// <summary>
    /// Uses a link up: removes the pending sign-up whose current link this is, if the link is still good, and returns it. One statement,
    /// so of parallel calls with one token exactly one gets the row.
    /// </summary>
    public static async Task<PendingSignup?> ConsumeAsync(AuthDbContext db, byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokenHash);

        var removed = await db.PendingSignups
            .FromSql($"""DELETE FROM "PendingSignups" WHERE "TokenHash" = {tokenHash} AND "TokenExpiresAt" > {now} RETURNING *""")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return removed.Count == 1 ? removed[0] : null;
    }

    public static Task DeleteAsync(AuthDbContext db, string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        return db.Database.ExecuteSqlAsync($"""DELETE FROM "PendingSignups" WHERE "NormalizedEmail" = {normalizedEmail}""", cancellationToken);
    }
}
