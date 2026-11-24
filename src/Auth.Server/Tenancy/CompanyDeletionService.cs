using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// Deletes a company (spec 0008 → Company deletion): the operator's <c>delete-org</c> and the company API's
/// <c>DELETE /auth/org</c> both call it. Everything happens under the company lock, in one transaction. The queued mails of the company's
/// invitations and the invitations go first, in the order the mail sender locks them (the mail row, then the invitation). Then every membership
/// of the company is deleted. A member who has no other space left, the caller included, loses the only space they had, so their account is
/// deleted next (spec 0010, <see cref="AccountDeletion"/>: the sessions and links go with it); a member who belongs to another space keeps the
/// account (spec 0015). Then the roles and the company (the keys from memberships and invitations to roles are <c>Restrict</c>, so the
/// memberships must go before the roles, and a single delete of the company would stop at them). A member who later presents a token of
/// this company: <c>403 permissions_changed</c> at the company API, <c>401 invalid_grant</c> at refresh; the <c>401</c> of an unknown address
/// at login when the account is gone.
/// </summary>
public sealed class CompanyDeletionService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, AccountDeletion deletion, AuditLog audit)
{
    /// <summary>
    /// Refused, in this order: <c>not_found</c> (no such company), <c>permissions_changed</c> (a member who no longer holds
    /// <c>org:delete</c>), the refusal of <paramref name="confirmInside"/>, <c>invalid_request</c> (<paramref name="confirmName"/> is not exactly the company's name, compared
    /// ordinally), <c>permission_not_held</c> (safety rule 1: deleting acts on every member, so a member of the company holds a
    /// permission the actor does not; the operator is not bound).
    /// </summary>
    /// <param name="confirmInside">
    /// Spec 0012, Amendment A12: the caller's confirmation that must be used up inside this transaction (a provider ticket), run right after the
    /// company lock and before the name check. It returns <see langword="null"/> to go on, or the error that refuses the deletion; a refusal of
    /// it or of any later check rolls back what it did.
    /// </param>
    public async Task<Outcome> DeleteAsync(
        Actor actor, Guid companyId, string confirmName, CancellationToken cancellationToken, Func<CancellationToken, Task<string?>>? confirmInside = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(confirmName);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.OrgDelete, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        if (confirmInside is not null && await confirmInside(cancellationToken) is { } refused)
        {
            return Outcome.Fail(refused);
        }

        var current = entered.Value!;
        var name = await audit.CompanyNameAsync(companyId, cancellationToken);
        if (name is null || !string.Equals(name, confirmName, StringComparison.Ordinal))
        {
            return Outcome.Fail(TenancyErrors.InvalidRequest);
        }

        // Rule 1, over everyone the deletion acts on: no member of the company may hold more than the actor.
        var catalog = manifest.Current.Catalog;
        var held = await db.Memberships.AsNoTracking()
            .Where(m => m.CompanyId == companyId)
            .Join(db.CompanyRoles, m => m.RoleId, r => r.Id, (m, r) => r.Permissions)
            .ToListAsync(cancellationToken);
        if (held.Any(permissions => !current.MayGrant(permissions, catalog)))
        {
            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
        }

        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.CompanyId == companyId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        // The invitations first, in the order the mail sender locks them: the mail row, then the invitation. Deleting the accounts first
        // would lock the invitations their members sent (the sender is set to null by the delete) before the mail rows, and the two could
        // wait for each other.
        var invitationIds = db.Invites.Where(i => i.CompanyId == companyId).Select(i => i.Id);
        await db.MailRequests
            .Where(r => r.InviteId != null && invitationIds.Contains(r.InviteId.Value))
            .ExecuteDeleteAsync(cancellationToken);
        var invitations = await db.Invites.Where(i => i.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);

        // Every membership of the company goes first (before the roles: a membership's key to its role restricts). A member left with no
        // other space, the caller included, then has their account deleted (spec 0010), one row each, in one set of statements, naming this
        // company; a member who has another space keeps the account and the sessions there (spec 0015). Under the company lock only, never
        // an address lock per member (spec 0010 → Locks): a large company could not be deleted otherwise.
        await db.Memberships.Where(m => m.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        var keepers = await db.Memberships.AsNoTracking()
            .Where(m => members.Contains(m.UserId))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        List<Guid> doomed = [.. members.Except(keepers)];
        if (doomed.Count > 0)
        {
            await deletion.DeleteAsync(doomed, AccountDeletionReason.OrgDeleted, current, companyId, cancellationToken);
        }

        var roles = await db.CompanyRoles.Where(r => r.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        await db.Companies.Where(c => c.Id == companyId).ExecuteDeleteAsync(cancellationToken);

        audit.Stage(
            new AuditEntry
            {
                Kind = AuditKinds.OrgDeleted,
                OrgId = companyId,
                OrgName = name,
                Details = new Dictionary<string, object?> { ["members"] = members.Count, ["invitations"] = invitations, ["roles"] = roles },
            },
            current);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }
}
