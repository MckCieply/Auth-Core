using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// Handles the mail queue (spec 0004 → Delivery). A pass first removes, in one statement, the due rows that will
/// never become a mail — no account, or nothing to confirm — so that requests for addresses without an account,
/// which anyone can make, cannot hold up the mails that matter. Then, for each remaining due row, in one transaction
/// with the row locked: issue a token, compose the mail and send it. The token is committed only once the server
/// has accepted the mail, so a failed attempt leaves the earlier link in force and no clear token is ever stored
/// (Decision 13).
/// </summary>
public sealed partial class MailDispatcher(
    IServiceScopeFactory scopes, TimeProvider clock, MailComposer composer, IMailTransport transport, ILogger<MailDispatcher> logger)
{
    /// <summary>Handles every row that is due now. Returns how many rows it handled.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        var handled = await DropUnmailableAsync(cancellationToken);
        while (await DispatchOneAsync(cancellationToken))
        {
            handled++;
        }

        return handled;
    }

    /// <summary>When the next row is due; <see langword="null"/> for an empty queue.</summary>
    public async Task<DateTimeOffset?> NextDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.MailRequests.MinAsync(r => (DateTimeOffset?)r.NextAttemptAt, cancellationToken);
    }

    /// <summary>
    /// Removes the due rows whose address has no account, or (for a verification) whose account is confirmed
    /// already. Those requests were answered with 202 like any other, and end here.
    /// </summary>
    private async Task<int> DropUnmailableAsync(CancellationToken cancellationToken)
    {
        var now = StorableTime.Now(clock);
        var passwordReset = (short)MailKind.PasswordReset;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM "MailRequests" WHERE "Id" IN (
                SELECT r."Id" FROM "MailRequests" r
                WHERE r."NextAttemptAt" <= {now}
                  AND NOT EXISTS (
                      SELECT 1 FROM "AspNetUsers" u
                      WHERE u."NormalizedEmail" = r."NormalizedEmail"
                        AND u."Email" IS NOT NULL
                        AND (r."Kind" = {passwordReset} OR NOT u."EmailConfirmed"))
                FOR UPDATE OF r SKIP LOCKED)
            """,
            cancellationToken);
    }

    private async Task<bool> DispatchOneAsync(CancellationToken cancellationToken)
    {
        var now = StorableTime.Now(clock);
        var givenUp = now - MailDelivery.GiveUpAfter;

        // A scope per row: nothing tracked for one row can leak into the next.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // A row is taken when it is due, or as soon as it is an hour old (to be dropped). SKIP LOCKED: a second
        // instance takes another row instead of waiting for this one.
        var due = await db.MailRequests
            .FromSql($"""
                SELECT * FROM "MailRequests"
                WHERE "NextAttemptAt" <= {now} OR "RequestedAt" <= {givenUp}
                ORDER BY "NextAttemptAt", "Id"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return false;
        }

        var request = due[0];
        if (request.RequestedAt <= givenUp)
        {
            LogGaveUp(logger, request.Id, request.Kind, request.Attempts);
            await RemoveAsync(db, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var user = await db.Users.AsNoTracking()
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == request.NormalizedEmail, cancellationToken);
        if (user?.Email is null || (request.Kind == MailKind.EmailVerification && user.EmailConfirmed))
        {
            // The account went away, or was confirmed, after the pass began.
            await RemoveAsync(db, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        // A failed send goes back to here: the new token is undone and the earlier link stays in force, while the
        // row stays locked until its next attempt has been written. Without the lock a second instance could take
        // the row in between and send it again.
        const string BeforeToken = "before_token";
        await transaction.CreateSavepointAsync(BeforeToken, cancellationToken);

        try
        {
            var token = await EmailTokens.IssueAsync(db, user.Id, request.Kind, now, cancellationToken);
            var mail = composer.Compose(request.Kind, user.Email, token);
            await transport.SendAsync(mail, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackToSavepointAsync(BeforeToken, CancellationToken.None);
            var attempts = request.Attempts + 1;
            // From the clock after the failure: a send that hung until its deadline must not leave the retry already due.
            var next = StorableTime.Now(clock) + MailDelivery.RetryDelay(attempts);
            await db.MailRequests.Where(r => r.Id == request.Id).ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.Attempts, attempts).SetProperty(r => r.NextAttemptAt, next),
                CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            LogSendFailed(logger, request.Id, request.Kind, attempts, exception.GetType().Name);
            return true;
        }

        await RemoveAsync(db, request, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task RemoveAsync(AuthDbContext db, MailRequest request, CancellationToken cancellationToken)
    {
        db.MailRequests.Remove(request);
        await db.SaveChangesAsync(cancellationToken);
    }

    // The type name of the failure only: an SMTP error text can quote the recipient.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending mail request {RequestId} ({Kind}) failed on attempt {Attempts}: {Failure}. It will be retried.")]
    private static partial void LogSendFailed(ILogger logger, long requestId, MailKind kind, int attempts, string failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mail request {RequestId} ({Kind}) was not delivered within the time limit and was dropped ({Attempts} failed attempts).")]
    private static partial void LogGaveUp(ILogger logger, long requestId, MailKind kind, int attempts);
}
