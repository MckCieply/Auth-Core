using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// The members of one company and what their roles hold, as they are inside the transaction that holds the company's
/// lock (spec 0005 → Safety rules, rule 2): enough to say how many managers the company has now, and how many it would
/// have after a change. A manager is a member whose role grants <c>members:manage</c>, directly or through <c>*</c>.
/// </summary>
public sealed class CompanyPeople
{
    private readonly List<(Guid UserId, Guid RoleId)> _members;
    private readonly Dictionary<Guid, string[]> _permissions;
    private readonly PermissionCatalog _catalog;

    private CompanyPeople(List<(Guid UserId, Guid RoleId)> members, Dictionary<Guid, string[]> permissions, PermissionCatalog catalog)
    {
        _members = members;
        _permissions = permissions;
        _catalog = catalog;
    }

    public static async Task<CompanyPeople> LoadAsync(AuthDbContext db, Guid companyId, PermissionCatalog catalog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(catalog);

        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.CompanyId == companyId)
            .Select(m => new { m.UserId, m.RoleId })
            .ToListAsync(cancellationToken);
        var roles = await db.CompanyRoles.AsNoTracking()
            .Where(r => r.CompanyId == companyId)
            .Select(r => new { r.Id, r.Permissions })
            .ToListAsync(cancellationToken);
        return new CompanyPeople([.. members.Select(m => (m.UserId, m.RoleId))], roles.ToDictionary(r => r.Id, r => r.Permissions), catalog);
    }

    /// <summary>Managers now.</summary>
    public int Managers() => Count(_ => null, _ => false);

    /// <summary>Managers if the member had the role <paramref name="roleId"/> instead.</summary>
    public int ManagersIfMemberHad(Guid userId, Guid roleId) =>
        Count(m => m.UserId == userId ? _permissions[roleId] : null, _ => false);

    /// <summary>Managers if the member were gone.</summary>
    public int ManagersWithout(Guid userId) => Count(_ => null, m => m.UserId == userId);

    /// <summary>Managers if the role held <paramref name="permissions"/> instead of what it holds.</summary>
    public int ManagersIfRoleHeld(Guid roleId, IEnumerable<string> permissions)
    {
        var replacement = permissions.ToArray();
        return Count(m => m.RoleId == roleId ? replacement : null, _ => false);
    }

    /// <summary>
    /// Safety rule 2: a change is refused when it leaves a company that has a manager without one. A company that has none
    /// already (the operator removed the last with <c>--force</c>) is not stopped from changing anything else.
    /// </summary>
    public static bool LeavesNone(int before, int after) => before > 0 && after == 0;

    private int Count(Func<(Guid UserId, Guid RoleId), string[]?> replacePermissions, Func<(Guid UserId, Guid RoleId), bool> gone) =>
        _members.Count(m => !gone(m)
            && _catalog.Expand(replacePermissions(m) ?? _permissions[m.RoleId]).Contains(PermissionCatalog.MembersManage));
}
