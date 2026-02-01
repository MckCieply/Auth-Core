using Auth.Infrastructure.Persistence;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// Takes a request for a mail: applies the limit of its address and kind and, if the request is within it, puts it
/// on the queue — one short transaction with the limit row locked, so parallel requests are each judged in turn. It
/// does the same work for every address and never looks an account up (spec 0004, Decision 12). It uses a scope of
/// its own, so the request's DbContext never tracks these rows.
/// </summary>
public sealed class MailRequestStore(IServiceScopeFactory scopes, TimeProvider clock)
{
    public async Task<MailLimitDecision> SubmitAsync(MailKind kind, string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        var now = StorableTime.Now(clock);
        var identifierHash = LoginIdentifier.HashOf(normalizedEmail);
        var kindValue = (short)kind;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // One statement creates the row or locks the existing one, and returns it either way (the pattern of
        // LoginStreakStore). ToListAsync, not SingleAsync: EF must send the statement as it is.
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
            db.MailRequests.Add(new MailRequest { Kind = kind, NormalizedEmail = normalizedEmail, RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return decision;
    }
}
