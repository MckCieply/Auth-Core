using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
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
    /// <param name="typedEmail">
    /// The address as it was typed. A password reset request is recorded in the audit log with it, in the same transaction as the
    /// queued request, and with no account: the request does not look the account up (spec 0004).
    /// </param>
    public async Task<MailLimitDecision> SubmitAsync(
        MailKind kind, string normalizedEmail, CancellationToken cancellationToken, string? typedEmail = null)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        var now = StorableTime.Now(clock);
        var identifierHash = LoginIdentifier.HashOf(normalizedEmail);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The limit row is created or locked by one statement (MailLimits), and returned either way.
        var decision = await MailLimits.RegisterAsync(db, identifierHash, kind, now, cancellationToken);
        if (decision.Allowed)
        {
            db.MailRequests.Add(new MailRequest { Kind = kind, NormalizedEmail = normalizedEmail, RequestedAt = now, NextAttemptAt = now });
            if (kind == MailKind.PasswordReset && typedEmail is not null)
            {
                scope.ServiceProvider.GetRequiredService<AuditLog>().Stage(
                    new AuditEntry { Kind = AuditKinds.PasswordResetRequested, SubjectEmail = typedEmail });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return decision;
    }
}
