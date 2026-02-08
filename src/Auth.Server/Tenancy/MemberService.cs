using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Tenancy;

/// <summary>
/// Lists the members of a company, changes their roles and removes them (spec 0005 → Company API, Safety rules, Effects).
/// The company API and the operator command both call it. Everything that changes anything runs in one transaction
/// that begins by locking the company, so that the last-manager check and the change cannot be interleaved with another.
/// </summary>
public sealed class MemberService(
    AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations)
{
    /// <summary>The company's members, sorted by address, ordinally.</summary>
    public async Task<IReadOnlyList<MemberItem>> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var rows = await (
            from membership in db.Memberships.AsNoTracking()
            join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
            join role in db.CompanyRoles.AsNoTracking() on membership.RoleId equals role.Id
            where membership.CompanyId == companyId
            select new { membership.UserId, user.Email, RoleId = role.Id, RoleName = role.Name, membership.JoinedAt })
            .ToListAsync(cancellationToken);

        return [.. rows
            .OrderBy(r => r.Email, StringComparer.Ordinal)
            .Select(r => new MemberItem(r.UserId, r.Email ?? "", new RoleRef(r.RoleId, r.RoleName), r.JoinedAt.UtcDateTime))];
    }

    /// <summary>
    /// Gives a member another role of the company. Refused: the member or the role is not in the company (<c>not_found</c>);
    /// the actor is the member (<c>cannot_change_self</c>, rule 3); the new role, or the member's current one, holds what the actor does not (rule 1); the change
    /// would leave the company without a manager (<c>last_manager</c>, rule 2). Ends no session: the member's next refresh
    /// carries the change.
    /// </summary>
    public async Task<Outcome> ChangeRoleAsync(Actor actor, Guid companyId, Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.MembersManage, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        var current = entered.Value!;
        var membership = await db.Memberships.FirstOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == userId, cancellationToken);
        var role = await db.CompanyRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId && r.CompanyId == companyId, cancellationToken);
        if (membership is null || role is null)
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        if (current.UserId == userId)
        {
            return Outcome.Fail(TenancyErrors.CannotChangeSelf);
        }

        // Rule 1, both ways: the role the member would get, and the role they have now.
        var catalog = manifest.Current.Catalog;
        if (!current.MayGrant(role.Permissions, catalog) || !await MayTouchAsync(current, membership, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
        }

        var people = await CompanyPeople.LoadAsync(db, companyId, catalog, cancellationToken);
        if (CompanyPeople.LeavesNone(people.Managers(), people.ManagersIfMemberHad(userId, roleId)))
        {
            return Outcome.Fail(TenancyErrors.LastManager);
        }

        membership.RoleId = roleId;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }

    /// <summary>
    /// Removes a member and ends every one of their sessions: every refresh token issued before is refused. The account stays,
    /// so the person can be invited again. Refused: not a member (<c>not_found</c>); the actor is the member
    /// (<c>cannot_change_self</c>); the member's role holds what the actor does not (<c>permission_not_held</c>, rule 1); the removal would leave the company without a manager (<c>last_manager</c>), unless
    /// <paramref name="force"/> — which only the operator has.
    /// </summary>
    public async Task<Outcome> RemoveAsync(Actor actor, Guid companyId, Guid userId, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.MembersManage, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        var current = entered.Value!;
        var membership = await db.Memberships.FirstOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        if (current.UserId == userId)
        {
            return Outcome.Fail(TenancyErrors.CannotChangeSelf);
        }

        // Rule 1 reaches the member's current role: nobody pushes out a member who holds more than they do.
        if (!await MayTouchAsync(current, membership, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
        }

        if (!force)
        {
            var people = await CompanyPeople.LoadAsync(db, companyId, manifest.Current.Catalog, cancellationToken);
            if (CompanyPeople.LeavesNone(people.Managers(), people.ManagersWithout(userId)))
            {
                return Outcome.Fail(TenancyErrors.LastManager);
            }
        }

        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(cancellationToken);

        // Every session ends: the refresh tokens, and the authorizations they hang on (spec 0002, Decision 11).
        var subject = userId.ToString();
        await tokens.RevokeBySubjectAsync(subject, cancellationToken);
        await authorizations.RevokeBySubjectAsync(subject, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }

    /// <summary>
    /// Safety rule 1, the other way round: whether the actor may remove this member or change their role. Not when the
    /// member's current role holds a permission the actor lacks, and a role with <c>*</c> only by an actor whose own role
    /// holds <c>*</c>. The operator may always.
    /// </summary>
    private async Task<bool> MayTouchAsync(Actor actor, Membership membership, CancellationToken cancellationToken)
    {
        var held = await db.CompanyRoles.AsNoTracking()
            .Where(r => r.Id == membership.RoleId)
            .Select(r => r.Permissions)
            .SingleAsync(cancellationToken);
        return actor.MayGrant(held, manifest.Current.Catalog);
    }
}
