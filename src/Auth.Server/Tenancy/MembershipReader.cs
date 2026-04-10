using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>A user's space and role as the database says now, with what the role grants against the active catalog.</summary>
/// <param name="Permissions">What the role grants: <c>*</c> expanded, no duplicates, sorted ordinally, never <c>*</c>.</param>
/// <param name="HoldsAll">Whether the role holds <c>*</c> itself, not merely every permission one by one. Never for a personal space.</param>
/// <param name="Kind">An organization, or the personal space of a private account (spec 0010).</param>
public sealed record TenantContext(
    Guid UserId, Guid CompanyId, string CompanyName, Guid RoleId, string RoleName, IReadOnlyList<string> Permissions, bool HoldsAll,
    CompanyKind Kind)
{
    public bool Holds(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);
}

/// <summary>
/// Reads a user's membership, role and the catalog afresh (spec 0005 → Access token): login, refresh, <c>GET /auth/me</c>
/// and every call of the company API go through it, so none of them trusts what a token said earlier. For a personal space (spec 0010) the
/// role's stored list is empty and the permissions are the manifest's <c>private.permissions</c>, read now: a manifest change reaches every
/// private account at its next refresh. Whatever the stored manifest holds, <c>*</c> and the company permissions are never granted to it.
/// </summary>
public sealed class MembershipReader(AuthDbContext db, ManifestHolder manifest)
{
    /// <summary>The user's membership; <see langword="null"/> when they belong to no space. With several, the earliest.</summary>
    public async Task<TenantContext?> ReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var row = await (
            from membership in db.Memberships.AsNoTracking()
            join company in db.Companies.AsNoTracking() on membership.CompanyId equals company.Id
            join role in db.CompanyRoles.AsNoTracking() on membership.RoleId equals role.Id
            where membership.UserId == userId
            orderby membership.JoinedAt
            select new { CompanyId = company.Id, CompanyName = company.Name, company.Kind, RoleId = role.Id, RoleName = role.Name, role.Permissions })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var current = manifest.Current;
        var personal = row.Kind == CompanyKind.Personal;
        var stored = personal
            ? current.PrivatePermissions.Where(p => p != PermissionCatalog.All && !PermissionCatalog.BuiltIn.Contains(p, StringComparer.Ordinal))
            : row.Permissions;
        return new TenantContext(
            userId,
            row.CompanyId,
            row.CompanyName,
            row.RoleId,
            row.RoleName,
            current.Catalog.Expand(stored),
            !personal && row.Permissions.Contains(PermissionCatalog.All, StringComparer.Ordinal),
            row.Kind);
    }
}
