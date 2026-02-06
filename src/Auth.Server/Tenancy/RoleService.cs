using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Requests;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// Lists, creates, replaces and deletes the roles of a company (spec 0005 → Company API, Safety rules). A role is a name and
/// a set of permissions from the catalog, or <c>*</c>. Everything that changes anything runs in one transaction that
/// begins by locking the company, so that the last-manager check and the change cannot be interleaved with another.
/// </summary>
public sealed class RoleService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, TimeProvider clock)
{
    /// <summary>
    /// The company's roles sorted by name, ordinally, each with the permissions it holds that are still in the catalog and
    /// the number of members who hold it, and the catalog: everything a role may hold, <c>*</c> first.
    /// </summary>
    public async Task<RolesResponse> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var catalog = manifest.Current.Catalog;
        var roles = await db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == companyId).ToListAsync(cancellationToken);
        var counts = await db.Memberships.AsNoTracking()
            .Where(m => m.CompanyId == companyId)
            .GroupBy(m => m.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.RoleId, g => g.Count, cancellationToken);

        return new RolesResponse(
            [.. roles
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .Select(r => new RoleItem(r.Id, r.Name, catalog.Visible(r.Permissions), counts.GetValueOrDefault(r.Id)))],
            catalog.Listed);
    }

    /// <summary>
    /// Creates a role. Refused: a name that breaks the rules (<c>invalid_request</c>); a permission outside the catalog
    /// (<c>unknown_permission</c>); a role that holds what the actor does not (<c>permission_not_held</c>, rule 1); a name
    /// the company has already, whatever its case (<c>role_name_taken</c>).
    /// </summary>
    public async Task<Outcome<RoleItem>> CreateAsync(
        Actor actor, Guid companyId, string name, IReadOnlyList<string> permissions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Validate(name, permissions) is { } invalid)
        {
            return Outcome.Fail<RoleItem>(invalid);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.RolesManage, cancellationToken);
        if (!entered.Succeeded)
        {
            return Outcome.Fail<RoleItem>(entered.Error!);
        }

        var catalog = manifest.Current.Catalog;
        var held = Normalize(permissions);
        if (!entered.Value!.MayGrant(held, catalog))
        {
            return Outcome.Fail<RoleItem>(TenancyErrors.PermissionNotHeld);
        }

        var normalizedName = NameInput.Normalize(name);
        if (await db.CompanyRoles.AnyAsync(r => r.CompanyId == companyId && r.NormalizedName == normalizedName, cancellationToken))
        {
            return Outcome.Fail<RoleItem>(TenancyErrors.RoleNameTaken);
        }

        var role = new CompanyRole { CompanyId = companyId, Name = name, NormalizedName = normalizedName, Permissions = held };
        db.CompanyRoles.Add(role);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Ok(new RoleItem(role.Id, role.Name, held, 0));
    }

    /// <summary>
    /// Replaces a role's name and permissions as a whole; what it held that is not in the request is gone, names that have
    /// left the catalog included. Refused like <see cref="CreateAsync"/> — rule 1 also looks at what the role holds now —
    /// and with <c>not_found</c> for a role that is not in the company and <c>last_manager</c> (rule 2) when the new permissions would leave the company without a manager.
    /// Ends no session: its members' next refresh carries the change.
    /// </summary>
    public async Task<Outcome> UpdateAsync(
        Actor actor, Guid companyId, Guid roleId, string name, IReadOnlyList<string> permissions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Validate(name, permissions) is { } invalid)
        {
            return Outcome.Fail(invalid);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.RolesManage, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        var role = await db.CompanyRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.CompanyId == companyId, cancellationToken);
        if (role is null)
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        // Rule 1, both ways: what the role would hold, and what it holds now.
        var catalog = manifest.Current.Catalog;
        var held = Normalize(permissions);
        if (!entered.Value!.MayGrant(held, catalog) || !entered.Value.MayGrant(role.Permissions, catalog))
        {
            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
        }

        var normalizedName = NameInput.Normalize(name);
        if (await db.CompanyRoles.AnyAsync(r => r.CompanyId == companyId && r.Id != roleId && r.NormalizedName == normalizedName, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.RoleNameTaken);
        }

        var people = await CompanyPeople.LoadAsync(db, companyId, catalog, cancellationToken);
        if (CompanyPeople.LeavesNone(people.Managers(), people.ManagersIfRoleHeld(roleId, held)))
        {
            return Outcome.Fail(TenancyErrors.LastManager);
        }

        role.Name = name;
        role.NormalizedName = normalizedName;
        role.Permissions = held;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }

    /// <summary>
    /// Deletes a role nobody holds. Refused with <c>permission_not_held</c> when the role holds a permission the actor
    /// lacks (rule 1), and with <c>role_in_use</c> while a member or a pending invitation has it; an expired
    /// invitation is not pending, and goes with the role.
    /// </summary>
    public async Task<Outcome> DeleteAsync(Actor actor, Guid companyId, Guid roleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var now = StorableTime.Now(clock);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.RolesManage, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        var role = await db.CompanyRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.CompanyId == companyId, cancellationToken);
        if (role is null)
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        // Rule 1: nobody deletes a role that holds more than they do.
        if (!entered.Value!.MayGrant(role.Permissions, manifest.Current.Catalog))
        {
            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
        }

        await db.Invites.Where(i => i.RoleId == roleId && i.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        if (await db.Memberships.AnyAsync(m => m.RoleId == roleId, cancellationToken)
            || await db.Invites.AnyAsync(i => i.RoleId == roleId, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.RoleInUse);
        }

        db.CompanyRoles.Remove(role);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }

    /// <summary>The refusal a name or a permission list deserves before the database is asked anything; <see langword="null"/> when there is none.</summary>
    private string? Validate(string name, IReadOnlyList<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(permissions);

        if (!NameInput.IsValid(name))
        {
            return TenancyErrors.InvalidRequest;
        }

        var catalog = manifest.Current.Catalog;
        return permissions.All(catalog.Accepts) ? null : TenancyErrors.UnknownPermission;
    }

    /// <summary>
    /// What is stored: <c>*</c> alone when it is there, since it already holds the rest; otherwise the permissions once each,
    /// sorted ordinally.
    /// </summary>
    private static string[] Normalize(IReadOnlyList<string> permissions) =>
        permissions.Contains(PermissionCatalog.All, StringComparer.Ordinal)
            ? [PermissionCatalog.All]
            : [.. permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
