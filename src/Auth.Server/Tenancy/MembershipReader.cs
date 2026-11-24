using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>A user's space and role as the database says now, with what the role grants against the active catalog.</summary>
/// <param name="Permissions">What the role grants: <c>*</c> expanded, no duplicates, sorted ordinally, never <c>*</c>.</param>
/// <param name="HoldsAll">Whether the role holds <c>*</c> itself, not merely every permission one by one. Never for a personal space.</param>
/// <param name="Kind">An organization, or the personal space of a private account (spec 0010).</param>
/// <param name="JoinedAt">When the user joined the space, as stored (spec 0015).</param>
public sealed record TenantContext(
    Guid UserId, Guid CompanyId, string CompanyName, Guid RoleId, string RoleName, IReadOnlyList<string> Permissions, bool HoldsAll,
    CompanyKind Kind, DateTimeOffset JoinedAt)
{
    public bool Holds(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);
}

/// <summary>
/// Reads a user's membership, role and the catalog afresh (spec 0005 → Access token): login, refresh, <c>GET /auth/me</c>
/// and every call of the company API go through it, so none of them trusts what a token said earlier. For a personal space (spec 0010) the
/// role's stored list is empty and the permissions are the manifest's <c>private.permissions</c>, read now: a manifest change reaches every
/// private account at its next refresh. Whatever the stored manifest holds, <c>*</c> and the company permissions are never granted to it.
/// It makes three reads (spec 0015): the space a sign-in lands in, the membership in one company, and the list and count of every membership.
/// </summary>
public sealed class MembershipReader(AuthDbContext db, ManifestHolder manifest)
{
    private sealed class Row
    {
        public required Guid CompanyId { get; init; }

        public required string CompanyName { get; init; }

        public required CompanyKind Kind { get; init; }

        public required Guid RoleId { get; init; }

        public required string RoleName { get; init; }

        public required string[] Permissions { get; init; }

        public required DateTimeOffset JoinedAt { get; init; }

        public required DateTimeOffset? LastUsedAt { get; init; }
    }

    /// <summary>
    /// The space a sign-in lands in (spec 0015 → Sign-in space); <see langword="null"/> when the user belongs to no space. In this order: a
    /// membership a sign-in or switch has chosen before the ones never chosen, the latest chosen first, then a company before a personal space,
    /// then the earliest joined, then the lower company id. The order is the database's.
    /// </summary>
    public async Task<TenantContext?> ReadSignInSpaceAsync(Guid userId, CancellationToken cancellationToken)
    {
        // PostgreSQL sorts DESC with nulls first, so "never used" is its own key: false (used) before true (never used).
        var row = await Rows(userId)
            .OrderBy(r => r.LastUsedAt == null)
            .ThenByDescending(r => r.LastUsedAt)
            .ThenBy(r => r.Kind)   // a company (1) before a personal space (2)
            .ThenBy(r => r.JoinedAt)
            .ThenBy(r => r.CompanyId)   // PostgreSQL's uuid order is that of the canonical text
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ToContext(userId, row);
    }

    /// <summary>The user's membership in one space, a company or their personal one; <see langword="null"/> when they have none there.</summary>
    public async Task<TenantContext?> ReadInCompanyAsync(Guid userId, Guid companyId, CancellationToken cancellationToken)
    {
        var row = await Rows(userId).Where(r => r.CompanyId == companyId).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ToContext(userId, row);
    }

    /// <summary>Every membership of the user, in no promised order: the caller orders.</summary>
    public async Task<IReadOnlyList<TenantContext>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await Rows(userId).ToListAsync(cancellationToken);
        return rows.ConvertAll(row => ToContext(userId, row));
    }

    /// <summary>How many spaces the user belongs to.</summary>
    public Task<int> CountAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Memberships.AsNoTracking().CountAsync(m => m.UserId == userId, cancellationToken);

    private IQueryable<Row> Rows(Guid userId) =>
        from membership in db.Memberships.AsNoTracking()
        join company in db.Companies.AsNoTracking() on membership.CompanyId equals company.Id
        join role in db.CompanyRoles.AsNoTracking() on membership.RoleId equals role.Id
        where membership.UserId == userId
        select new Row
        {
            CompanyId = company.Id,
            CompanyName = company.Name,
            Kind = company.Kind,
            RoleId = role.Id,
            RoleName = role.Name,
            Permissions = role.Permissions,
            JoinedAt = membership.JoinedAt,
            LastUsedAt = membership.LastUsedAt,
        };

    private TenantContext ToContext(Guid userId, Row row)
    {
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
            row.Kind,
            row.JoinedAt);
    }
}
