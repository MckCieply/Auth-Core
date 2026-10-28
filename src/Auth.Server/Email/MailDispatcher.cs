using Auth.Infrastructure.Persistence;
using Auth.Server.Requests;
using Auth.Server.Signup;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// Handles the mail queue (spec 0004 → Delivery). A pass first removes, in one statement, the due rows that will
/// never become a mail — no account, or nothing to confirm — so that requests for addresses without an account,
/// which anyone can make, cannot hold up the mails that matter. Then, for each due row that can become a mail, in one
/// transaction with the row locked: issue a token, compose the mail and send it. Only such rows (and rows an hour
/// old, which are dropped) are handled singly: requests that need no mail and arrive during the pass wait for the
/// bulk delete of the next one. The token is committed only once the server has accepted the mail, so a failed
/// attempt leaves the earlier link in force and no clear token is ever stored (Decision 13). An invitation request
/// (spec 0005) names an invitation, not an account: its mail goes to the address on the invitation, the token is
/// issued into the invitation, and the request is never dropped for want of an account.
/// A sign-up request (specs 0009 and 0010) names a pending sign-up, not an account, and is decided here: the switch of its kind first, then any
/// account under the address gets the mail that says so (no link), and otherwise a live pending sign-up gets a new link in the variant of its kind;
/// with neither there is nothing to send. A request queued before the manifest or the settings turned its kind off is dropped when it comes up.
/// </summary>
public sealed partial class MailDispatcher(
    IServiceScopeFactory scopes, TimeProvider clock, MailComposer composer, IMailTransport transport, SignupGate gate, ILogger<MailDispatcher> logger)
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
    /// already; a sign-up row is removed when its live pending sign-up's kind is switched off, or, with no live pending sign-up, when both
    /// switches are off or the address has no account (spec 0010 → Mails). Those requests were answered with 202 like any other, and end here.
    /// </summary>
    private async Task<int> DropUnmailableAsync(CancellationToken cancellationToken)
    {
        var now = StorableTime.Now(clock);
        var passwordReset = (short)MailKind.PasswordReset;
        var invitation = (short)MailKind.Invitation;
        var signup = (short)MailKind.Signup;
        var organization = (short)SignupKind.Organization;
        var privateKind = (short)SignupKind.Private;
        var organizationOn = gate.IsOn;
        var privateOn = gate.PrivateOn;
        var anyOn = gate.AnyOn;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM "MailRequests" WHERE "Id" IN (
                SELECT r."Id" FROM "MailRequests" r
                WHERE r."NextAttemptAt" <= {now}
                  AND r."Kind" <> {invitation}
                  AND ((r."Kind" = {signup}
                        AND (EXISTS (
                                 SELECT 1 FROM "PendingSignups" p
                                 WHERE p."NormalizedEmail" = r."NormalizedEmail" AND p."ExpiresAt" > {now}
                                   AND ((p."Kind" = {organization} AND NOT {organizationOn}) OR (p."Kind" = {privateKind} AND NOT {privateOn})))
                             OR (NOT EXISTS (
                                     SELECT 1 FROM "PendingSignups" p
                                     WHERE p."NormalizedEmail" = r."NormalizedEmail" AND p."ExpiresAt" > {now})
                                 AND (NOT {anyOn}
                                      OR NOT EXISTS (
                                          SELECT 1 FROM "AspNetUsers" a
                                          WHERE a."NormalizedEmail" = r."NormalizedEmail" AND a."Email" IS NOT NULL)))))
                       OR (r."Kind" <> {signup}
                           AND NOT EXISTS (
                               SELECT 1 FROM "AspNetUsers" u
                               WHERE u."NormalizedEmail" = r."NormalizedEmail"
                                 AND u."Email" IS NOT NULL
                                 AND (r."Kind" = {passwordReset} OR NOT u."EmailConfirmed"))))
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

        // Only a row that can become a mail is taken when it is due (an account to mail, and for a verification
        // one still to confirm; for a sign-up, a live pending sign-up, or with none an account (the switch is checked when it is sent); an invitation always), or any
        // row as soon as it is an hour old (to be dropped). A row that needs no mail
        // is left for the bulk delete of the next pass, so that a flood of such requests arriving during this pass
        // is never handled one by one ahead of the real mails. SKIP LOCKED: a second instance takes another row
        // instead of waiting for this one.
        var passwordReset = (short)MailKind.PasswordReset;
        var invitation = (short)MailKind.Invitation;
        var signup = (short)MailKind.Signup;
        var due = await db.MailRequests
            .FromSql($"""
                SELECT r.* FROM "MailRequests" r
                WHERE (r."NextAttemptAt" <= {now}
                       AND (r."Kind" = {invitation}
                            OR (r."Kind" = {signup}
                                AND (EXISTS (
                                         SELECT 1 FROM "PendingSignups" p
                                         WHERE p."NormalizedEmail" = r."NormalizedEmail" AND p."ExpiresAt" > {now})
                                     OR EXISTS (
                                         SELECT 1 FROM "AspNetUsers" a
                                         WHERE a."NormalizedEmail" = r."NormalizedEmail" AND a."Email" IS NOT NULL)))
                            OR (r."Kind" <> {signup}
                                AND EXISTS (
                                    SELECT 1 FROM "AspNetUsers" u
                                    WHERE u."NormalizedEmail" = r."NormalizedEmail"
                                      AND u."Email" IS NOT NULL
                                      AND (r."Kind" = {passwordReset} OR NOT u."EmailConfirmed")))))
                   OR r."RequestedAt" <= {givenUp}
                ORDER BY r."NextAttemptAt", r."Id"
                LIMIT 1
                FOR UPDATE OF r SKIP LOCKED
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

        // What it takes to make this request's mail, or nothing when there is no mail to make any more.
        var makeMail = request.Kind switch
        {
            MailKind.Invitation => await PrepareInvitationAsync(db, request, now, cancellationToken),
            MailKind.Signup => await PrepareSignupMailAsync(db, request, now, cancellationToken),
            _ => await PrepareAccountMailAsync(db, request, now, cancellationToken),
        };
        if (makeMail is null)
        {
            // The account went away, or was confirmed, or the invitation was cancelled, or the pending sign-up expired or
            // was completed with no account under the address, after the pass began, or its kind of sign-up is off.
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
            await transport.SendAsync(await makeMail(), cancellationToken);
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

    /// <summary>The mail of a reset or a verification: to the stored address of the account, with a token issued to it.</summary>
    private async Task<Func<Task<ComposedMail>>?> PrepareAccountMailAsync(
        AuthDbContext db, MailRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == request.NormalizedEmail, cancellationToken);
        if (user?.Email is null || (request.Kind == MailKind.EmailVerification && user.EmailConfirmed))
        {
            return null;
        }

        return async () =>
        {
            var token = await EmailTokens.IssueAsync(db, user.Id, request.Kind, now, cancellationToken);
            return composer.Compose(request.Kind, user.Email, token);
        };
    }

    /// <summary>
    /// The mail of a sign-up (specs 0009 and 0010 → Mails): decided here, when it is sent, and never by the request. The live pending sign-up of
    /// the address is locked first (a completion waits for the mail; the deletion of an account deletes the mail requests before the pending
    /// sign-up, so the two never wait for each other the other way round). The switch is checked first: while the pending sign-up exists the
    /// switch of its kind must be on; when it is gone, at least one switch. Otherwise nothing is sent and the request is dropped. Then any account
    /// under the address, of either kind and under any spelling the normaliser folds together, read under the lock, gets the mail that says so,
    /// to the address as the account stores it, with no link, and the pending sign-up is deleted in the transaction of the send (a failed send
    /// keeps it). Otherwise a live pending sign-up gets a new link token (the earlier link stops working) and the mail in the variant of its kind,
    /// to the address as typed. With neither, there is nothing to send.
    /// </summary>
    private async Task<Func<Task<ComposedMail>>?> PrepareSignupMailAsync(
        AuthDbContext db, MailRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = await PendingSignups.LockUsableAsync(db, request.NormalizedEmail, now, cancellationToken);
        if (pending is null ? !gate.AnyOn : !gate.IsOnFor(pending.Kind))
        {
            return null;   // switched off after the request: no mail of it, the notice included
        }

        var account = await SignupAccounts.FindAsync(db, request.NormalizedEmail, cancellationToken);
        if (account is not null)
        {
            return AccountExistsMail(db, request.NormalizedEmail, account.Email, cancellationToken);
        }

        if (pending is null)
        {
            return null;
        }

        return async () =>
        {
            var token = await PendingSignups.IssueAsync(db, pending.NormalizedEmail, now, cancellationToken);
            return pending.Kind == SignupKind.Private
                ? composer.Compose(MailKind.Signup, pending.Email, token, privateAccount: true)
                : composer.Compose(MailKind.Signup, pending.Email, token, pending.CompanyName);
        };
    }

    private Func<Task<ComposedMail>> AccountExistsMail(AuthDbContext db, string normalizedEmail, string storedAddress, CancellationToken cancellationToken) =>
        async () =>
        {
            await PendingSignups.DeleteAsync(db, normalizedEmail, cancellationToken);
            return composer.ComposeAccountExists(storedAddress);
        };

    /// <summary>
    /// The mail of an invitation (spec 0005): to the address on the invitation, which may have no account, with a token
    /// issued to the invitation. The invitation row is locked, so that cancelling or accepting it waits for the mail.
    /// </summary>
    private async Task<Func<Task<ComposedMail>>?> PrepareInvitationAsync(
        AuthDbContext db, MailRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (request.InviteId is not { } inviteId)
        {
            return null;
        }

        var invites = await db.Invites
            .FromSql($"""SELECT * FROM "Invites" WHERE "Id" = {inviteId} FOR UPDATE""")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (invites.Count == 0)
        {
            return null;
        }

        var invite = invites[0];
        var companyName = await db.Companies.AsNoTracking().Where(c => c.Id == invite.CompanyId).Select(c => c.Name).SingleAsync(cancellationToken);

        // Spec 0010: an address that has a private account, spelled as the invitation spells it up to ASCII case, is told what accepting does to
        // it. Decided now, when the mail is composed; the preview decides again when the link is used.
        var account = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == invite.NormalizedEmail)
            .OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.Email })
            .FirstOrDefaultAsync(cancellationToken);
        var privateAccount = account is { Email: { } stored }
            && EmailInput.IsSameAddress(stored, invite.Email)
            && await (
                from membership in db.Memberships.AsNoTracking()
                join space in db.Companies.AsNoTracking() on membership.CompanyId equals space.Id
                where membership.UserId == account.Id && space.Kind == CompanyKind.Personal
                select membership.UserId).AnyAsync(cancellationToken);
        return async () =>
        {
            var token = await InviteTokens.IssueAsync(db, invite.Id, now, cancellationToken);
            return composer.Compose(MailKind.Invitation, invite.Email, token, companyName, privateAccount);
        };
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
