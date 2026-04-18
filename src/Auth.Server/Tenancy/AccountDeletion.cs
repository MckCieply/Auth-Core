using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.Server.Tenancy;

/// <summary>Why an account is deleted (spec 0010 → Accounts are never left without a space); its name is <c>details.reason</c>.</summary>
public enum AccountDeletionReason
{
    Self,
    MemberRemoved,
    OrgDeleted,
    Upgrade,
}

/// <summary>
/// Deletes accounts (spec 0010 → What an account deletion removes): the one routine behind a self-deletion, the removal of a
/// member, the deletion of a company and the start-up step. It runs in the caller's transaction, under the caller's locks, by
/// set-based statements, and stages one <c>account.deleted</c> row per account for the caller's save. It needs the caller's
/// transaction (it throws without one).
/// <para>
/// The order matters, because the mail sender locks a mail request first and then, by the kind of mail, either the pending sign-up
/// (a sign-up mail) or the account row (the link of a reset or verification mail, whose foreign key takes a share lock on it); any
/// other order can deadlock. So the routine first deletes the queued mail requests of the addresses other than invitations, then
/// the pending sign-ups of the addresses, without any account row locked. Only then does it read the accounts under a row lock
/// that is held to the end of the transaction, and delete, for each account: its login streak, its refresh tokens and then their
/// authorizations (deleted, not only revoked: they name the account; there is no cascade between them), the account (its Identity
/// rows, its mail links and its membership go by cascade; invitations it sent lose their sender) and, for a personal space, the
/// space with its role row. The row lock precedes the deletes of the sessions, so that no sign-in or refresh writes a session of
/// an account it is deleting (they read their account again under a share lock first).
/// </para>
/// <para>
/// It keeps the audit rows, the invitations of companies to the address and the mail-limit counters. It relies on the caller's
/// locks for the rest: when the delete removes another number of accounts than the read found, it throws and the caller's
/// transaction rolls back.
/// </para>
/// </summary>
public sealed class AccountDeletion(AuthDbContext db, AuditLog audit)
{
    public static string NameOf(AccountDeletionReason reason) => reason switch
    {
        AccountDeletionReason.Self => "self",
        AccountDeletionReason.MemberRemoved => "member_removed",
        AccountDeletionReason.OrgDeleted => "org_deleted",
        AccountDeletionReason.Upgrade => "upgrade",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown reason."),
    };

    /// <summary>Deletes one account; see the other overload.</summary>
    public Task<int> DeleteAsync(Guid userId, AccountDeletionReason reason, Actor? actor, CancellationToken cancellationToken) =>
        DeleteAsync([userId], reason, actor, cancellationToken);

    /// <summary>
    /// Deletes the accounts; returns how many there were (one already gone is skipped, with no row). <paramref name="actor"/> is the admin who
    /// removes a member or deletes the company, <see cref="Actor.Operator"/> for the CLI, or <see langword="null"/>; for
    /// <see cref="AccountDeletionReason.Self"/> the account itself is the actor of its row.
    /// </summary>
    public async Task<int> DeleteAsync(
        IReadOnlyCollection<Guid> userIds, AccountDeletionReason reason, Actor? actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Deleting accounts needs the caller's transaction: the statements must commit together with the audit rows.");
        }

        List<Guid> ids = [.. userIds.Distinct()];

        // The mail requests and the pending sign-ups of the addresses first, before any account row is locked: the mail sender locks a mail
        // request and then needs the account row, so the other order could wait forever. The addresses are read inside each statement, so the
        // count of statements does not depend on the number of accounts.
        var addressesOf = db.Users.Where(u => ids.Contains(u.Id)).Select(u => u.NormalizedEmail);
        await db.MailRequests
            .Where(r => r.Kind != MailKind.Invitation && addressesOf.Contains(r.NormalizedEmail))
            .ExecuteDeleteAsync(cancellationToken);
        await db.PendingSignups.Where(p => addressesOf.Contains(p.NormalizedEmail)).ExecuteDeleteAsync(cancellationToken);

        // The accounts, read under a row lock held to the end of the transaction: from here on no sign-in or refresh can write a session for them.
        Guid[] wanted = [.. ids];
        var accounts = await db.Users
            .FromSql($"""SELECT * FROM "AspNetUsers" WHERE "Id" = ANY({wanted}) ORDER BY "Id" FOR UPDATE""")
            .AsNoTracking()
            .Select(u => new { u.Id, u.Email, u.NormalizedEmail })
            .ToListAsync(cancellationToken);
        if (accounts.Count == 0)
        {
            return 0;
        }

        List<Guid> found = [.. accounts.Select(a => a.Id)];

        // The space each account loses, read before anything is deleted: the row names it.
        var spaces = await (
            from membership in db.Memberships.AsNoTracking()
            join company in db.Companies.AsNoTracking() on membership.CompanyId equals company.Id
            where found.Contains(membership.UserId)
            orderby company.Kind, membership.JoinedAt   // a company (1) before a personal space (2), as everywhere
            select new { membership.UserId, company.Id, company.Name, company.Kind })
            .ToListAsync(cancellationToken);
        var lost = spaces.GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.First());

        List<string> addresses = [.. accounts.Select(a => a.NormalizedEmail).OfType<string>().Distinct(StringComparer.Ordinal)];
        List<byte[]> streaks = [.. addresses.Select(LoginIdentifier.HashOf)];
        await db.LoginStreaks.Where(s => streaks.Contains(s.IdentifierHash)).ExecuteDeleteAsync(cancellationToken);

        List<string> subjects = [.. found.Select(id => id.ToString())];
        await db.Set<OpenIddictEntityFrameworkCoreToken>()
            .Where(t => t.Subject != null && subjects.Contains(t.Subject))
            .ExecuteDeleteAsync(cancellationToken);
        await db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
            .Where(a => a.Subject != null && subjects.Contains(a.Subject))
            .ExecuteDeleteAsync(cancellationToken);

        var removed = await db.Users.Where(u => found.Contains(u.Id)).ExecuteDeleteAsync(cancellationToken);
        if (removed != accounts.Count)
        {
            throw new InvalidOperationException(
                $"The accounts changed while they were being deleted: {accounts.Count} were read and {removed} were removed. The caller's locks must keep them from changing.");
        }

        List<Guid> personal = [.. spaces.Where(s => s.Kind == CompanyKind.Personal).Select(s => s.Id).Distinct()];
        if (personal.Count > 0)
        {
            await db.Companies.Where(c => personal.Contains(c.Id) && c.Kind == CompanyKind.Personal).ExecuteDeleteAsync(cancellationToken);
        }

        foreach (var account in accounts)
        {
            lost.TryGetValue(account.Id, out var space);
            audit.Stage(
                new AuditEntry
                {
                    Kind = AuditKinds.AccountDeleted,
                    ActorUserId = reason == AccountDeletionReason.Self ? account.Id : null,
                    SubjectUserId = account.Id,
                    SubjectEmail = account.Email,
                    OrgId = space?.Id,
                    OrgName = space?.Name,
                    Details = new Dictionary<string, object?> { ["reason"] = NameOf(reason) },
                },
                actor);
        }

        return accounts.Count;
    }
}
