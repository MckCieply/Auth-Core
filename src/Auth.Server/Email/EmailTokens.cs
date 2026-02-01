using System.Security.Cryptography;
using System.Text;
using Auth.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// The link tokens of spec 0004: random values that carry nothing, stored as hashes, single-use and expiring
/// (Decision 4). Both operations run in the caller's context, so the caller's transaction decides whether they
/// happened: a reset that fails after consuming its token rolls the consumption back with everything else.
/// </summary>
public static class EmailTokens
{
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    public static readonly TimeSpan VerificationLifetime = TimeSpan.FromHours(24);

    public static TimeSpan LifetimeOf(MailKind kind) => kind switch
    {
        MailKind.PasswordReset => ResetLifetime,
        MailKind.EmailVerification => VerificationLifetime,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of mail."),
    };

    public static byte[] HashOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>
    /// Replaces the user's token of this kind by a new one and returns it in clear: 32 random bytes as 43
    /// base64url characters. The caller puts it into a mail and nowhere else. One statement on the row the table
    /// keeps per user and kind, so two callers at once still leave exactly one token, the later one's.
    /// </summary>
    public static async Task<string> IssueAsync(
        AuthDbContext db, Guid userId, MailKind kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var hash = HashOf(token);
        var kindValue = (short)kind;
        var expiresAt = now + LifetimeOf(kind);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "EmailTokens" ("UserId", "Kind", "TokenHash", "ExpiresAt")
            VALUES ({userId}, {kindValue}, {hash}, {expiresAt})
            ON CONFLICT ("UserId", "Kind") DO UPDATE SET "TokenHash" = EXCLUDED."TokenHash", "ExpiresAt" = EXCLUDED."ExpiresAt"
            """,
            cancellationToken);
        return token;
    }

    /// <summary>
    /// Uses a token up: removes its row if it is of this kind and not expired, and returns the user it belongs to;
    /// <see langword="null"/> for anything else. One statement, so of parallel calls with one token exactly one
    /// gets the user.
    /// </summary>
    public static async Task<Guid?> ConsumeAsync(
        AuthDbContext db, string token, MailKind kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(token);

        var hash = HashOf(token);
        var kindValue = (short)kind;

        // ToListAsync, not SingleOrDefaultAsync: EF must send the statement as it is, not wrapped in a subquery.
        var removed = await db.EmailTokens
            .FromSql($"""
                DELETE FROM "EmailTokens"
                WHERE "TokenHash" = {hash} AND "Kind" = {kindValue} AND "ExpiresAt" > {now}
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return removed.Count == 1 ? removed[0].UserId : null;
    }
}
