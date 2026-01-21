using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Lockout;

/// <summary>
/// Persists login streaks. Counting an attempt is one short transaction with the identifier's row locked, so
/// parallel attempts each get their own number in the streak. It uses a scope of its own: the transaction is over
/// before the caller hashes a password, and the request's own DbContext never tracks a streak row.
/// </summary>
public sealed class LoginStreakStore(IServiceScopeFactory scopes, TimeProvider clock)
{
    public async Task<AttemptDecision> RegisterAttemptAsync(byte[] identifierHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identifierHash);

        var now = StorablePrecision(clock.GetUtcNow());
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // One statement creates the row or locks the existing one, and returns it either way. The no-op update is
        // what takes the row lock; a parallel attempt, or the pruner's delete, waits on it until this transaction ends.
        // ToListAsync, not SingleAsync: EF must send the statement as it is, not wrapped in a subquery.
        var rows = await db.LoginStreaks
            .FromSql($"""
                INSERT INTO "LoginStreaks" ("IdentifierHash", "AttemptCount", "LastAttemptAt", "LockedUntil", "BurstStartedAt", "BurstCount")
                VALUES ({identifierHash}, 0, {now}, NULL, {now}, 0)
                ON CONFLICT ("IdentifierHash") DO UPDATE SET "AttemptCount" = "LoginStreaks"."AttemptCount"
                RETURNING *
                """)
            .ToListAsync(cancellationToken);
        var row = rows.Single();

        var (next, decision) = LockoutPolicy.Register(
            new StreakState(row.AttemptCount, row.LastAttemptAt, row.LockedUntil, row.BurstStartedAt, row.BurstCount), now);
        row.AttemptCount = next.AttemptCount;
        row.LastAttemptAt = next.LastAttemptAt;
        row.LockedUntil = next.LockedUntil;
        row.BurstStartedAt = next.BurstStartedAt;
        row.BurstCount = next.BurstCount;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return decision;
    }

    /// <summary>Ends the streak of an identifier (a successful login). No row is not an error.</summary>
    public async Task ClearAsync(byte[] identifierHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identifierHash);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.LoginStreaks.Where(s => s.IdentifierHash == identifierHash).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>PostgreSQL keeps microseconds. Compute on what the row can hold, so a stored instant reads back equal.</summary>
    private static DateTimeOffset StorablePrecision(DateTimeOffset instant) =>
        instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerMicrosecond));
}
