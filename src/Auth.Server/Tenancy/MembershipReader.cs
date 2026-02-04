using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>A user's company and role as the database says now, with what the role grants against the active catalog.</summary>
/// <param name="Permissions">What the role grants: <c>*</c> expanded, no duplicates, sorted ordinally, never <c>*</c>.</param>
/// <param name="HoldsAll">Whether the role holds <c>*</c> itself, not merely every permission one by one.</param>
public sealed record TenantContext(
    Guid UserId, Guid CompanyId, string CompanyName, Guid RoleId, string RoleName, IReadOnlyList<string> Permissions, bool HoldsAll)
{
    public bool Holds(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);
}

/// <summary>
/// Reads a user's membership, role and the catalog afresh (spec 0005 → Access token): login, refresh, <c>GET /auth/me</c>
/// and every call of the company API go through it, so none of them trusts what a token said earlier.
/// </summary>
public sealed class MembershipReader(AuthDbContext db, ManifestHolder manifest)
{
    /// <summary>The user's membership; <see langword="null"/> when they belong to no company. With several, the earliest.</summary>
    public async Task<TenantContext?> ReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var row = await (
            from membership in db.Memberships.AsNoTracking()
            join company in db.Companies.AsNoTracking() on membership.CompanyId equals company.Id
            join role in db.CompanyRoles.AsNoTracking() on membership.RoleId equals role.Id
            where membership.UserId == userId
            orderby membership.JoinedAt
            select new { CompanyId = company.Id, CompanyName = company.Name, RoleId = role.Id, RoleName = role.Name, role.Permissions })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        return new TenantContext(
            userId,
            row.CompanyId,
            row.CompanyName,
            row.RoleId,
            row.RoleName,
            manifest.Current.Catalog.Expand(row.Permissions),
            row.Permissions.Contains(PermissionCatalog.All, StringComparer.Ordinal));
    }
}
