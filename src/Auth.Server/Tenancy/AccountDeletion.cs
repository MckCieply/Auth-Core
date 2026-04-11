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
/// Deletes accounts (spec 0010 → What an account deletion removes): the one routine behind a self-deletion, the removal of a member, the deletion
/// of a company and the start-up step. It runs in the caller's transaction, under the caller's locks, by set-based statements, and stages one
/// <c>account.deleted</c> row per account for the caller's save. For each account it deletes: the queued mail requests of its address other than
/// invitations, then the pending sign-up of its address (the mail sender locks them in that order; the other one can deadlock), its login streak,
/// its refresh tokens and then their authorizations (deleted, not only revoked: they name the account; there is no cascade between them), the
/// account (its Identity rows, its mail links and its membership go by cascade; invitations it sent lose their sender) and, for a personal space,
/// the space with its role row. It keeps the audit rows, the invitations of companies to the address and the mail-limit counters. It needs the
/// caller's transaction (it throws without one) and relies on the caller's locks to keep the accounts from changing between its read and its
/// delete: when the delete removes another number of accounts than the read found, it throws and the caller's transaction rolls back.
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
        var accounts = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
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
            orderby membership.JoinedAt
            select new { membership.UserId, company.Id, company.Name, company.Kind })
            .ToListAsync(cancellationToken);
        var lost = spaces.GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.First());

        List<string> addresses = [.. accounts.Select(a => a.NormalizedEmail).OfType<string>().Distinct(StringComparer.Ordinal)];
        await db.MailRequests
            .Where(r => addresses.Contains(r.NormalizedEmail) && r.Kind != MailKind.Invitation)
            .ExecuteDeleteAsync(cancellationToken);
        await db.PendingSignups.Where(p => addresses.Contains(p.NormalizedEmail)).ExecuteDeleteAsync(cancellationToken);
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
