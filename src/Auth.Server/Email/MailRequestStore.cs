using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Auth.Server.Signup;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// Takes a request for a mail: applies the limit of its address and kind and, if the request is within it, puts it
/// on the queue — one short transaction with the limit row locked, so parallel requests are each judged in turn. It
/// does the same work for every address and never looks an account up (spec 0004, Decision 12; spec 0009 for a sign-up). It uses a scope of
/// its own, so the request's DbContext never tracks these rows.
/// </summary>
public sealed class MailRequestStore(IServiceScopeFactory scopes, TimeProvider clock)
{
    /// <param name="typedEmail">
    /// The address as it was typed. A password reset request is recorded in the audit log with it, in the same transaction as the
    /// queued request, and with no account: the request does not look the account up (spec 0004). A sign-up (spec 0009) needs it: it is
    /// the address of the pending sign-up and of its audit row.
    /// </param>
    /// <param name="companyName">The company name of an organization sign-up; required for it, ignored otherwise.</param>
    /// <param name="signupKind">The kind of a sign-up request (spec 0010); a private one carries no company name. Ignored for other mails.</param>
    public async Task<MailLimitDecision> SubmitAsync(
        MailKind kind, string normalizedEmail, CancellationToken cancellationToken, string? typedEmail = null, string? companyName = null,
        SignupKind signupKind = SignupKind.Organization)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        if (kind == MailKind.Signup && (typedEmail is null || (signupKind == SignupKind.Organization && companyName is null)))
        {
            throw new ArgumentException(
                "A sign-up request carries the address as typed, and an organization sign-up its company name.",
                typedEmail is null ? nameof(typedEmail) : nameof(companyName));
        }

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
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
            if (kind == MailKind.PasswordReset && typedEmail is not null)
            {
                audit.Stage(new AuditEntry { Kind = AuditKinds.PasswordResetRequested, SubjectEmail = typedEmail });
            }
            else if (kind == MailKind.Signup && typedEmail is not null)
            {
                // The pending sign-up takes the place an account would have: it is what the mail, when it is sent, is about. One
                // statement, whether the address has an account or not (which the request never learns). The row records its kind.
                await PendingSignups.UpsertAsync(
                    db, normalizedEmail, typedEmail, signupKind == SignupKind.Private ? null : companyName, signupKind, now, cancellationToken);
                audit.Stage(new AuditEntry
                {
                    Kind = AuditKinds.SignupRequested,
                    SubjectEmail = typedEmail,
                    Details = new Dictionary<string, object?> { ["kind"] = SignupKinds.NameOf(signupKind) },
                });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return decision;
    }
}
