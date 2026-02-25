using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Tenancy;

/// <summary>
/// Deletes a company (spec 0008 → Company deletion): the operator's <c>delete-org</c> and the company API's
/// <c>DELETE /auth/org</c> both call it. Everything happens under the company lock, in one transaction. The queued mails of the
/// company's invitations, the invitations, the memberships, the roles and the company are deleted in that order (the keys from
/// memberships and invitations to roles are <c>Restrict</c>, so a single delete of the company would stop at them). Every member's
/// sessions are revoked, as removing a member does; the accounts stay. A member who later presents a token is treated as a removed
/// member: <c>403 permissions_changed</c> at the company API, <c>401 invalid_grant</c> at refresh, <c>403 no_membership</c> at login.
/// </summary>
public sealed class CompanyDeletionService(
    AuthDbContext db, CompanyGuard guard, ManifestHolder manifest,
    IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, AuditLog audit)
{
    /// <summary>
    /// Refused, in this order: <c>not_found</c> (no such company), <c>permissions_changed</c> (a member who no longer holds
    /// <c>org:delete</c>), <c>invalid_request</c> (<paramref name="confirmName"/> is not exactly the company's name, compared
    /// ordinally), <c>permission_not_held</c> (safety rule 1: deleting acts on every member, so a member of the company holds a
    /// permission the actor does not; the operator is not bound).
    /// </summary>
    public async Task<Outcome> DeleteAsync(Actor actor, Guid companyId, string confirmName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(confirmName);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.OrgDelete, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
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
        var invitationIds = db.Invites.Where(i => i.CompanyId == companyId).Select(i => i.Id);
        await db.MailRequests
            .Where(r => r.InviteId != null && invitationIds.Contains(r.InviteId.Value))
            .ExecuteDeleteAsync(cancellationToken);
        var invitations = await db.Invites.Where(i => i.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        await db.Memberships.Where(m => m.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        var roles = await db.CompanyRoles.Where(r => r.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        await db.Companies.Where(c => c.Id == companyId).ExecuteDeleteAsync(cancellationToken);

        // Every session of every member ends (spec 0002, Decision 11), as removing a member does.
        foreach (var member in members)
        {
            var subject = member.ToString();
            await tokens.RevokeBySubjectAsync(subject, cancellationToken);
            await authorizations.RevokeBySubjectAsync(subject, cancellationToken);
        }

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
