using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Tenancy;

/// <summary>The caller of a company API endpoint, or the answer that refuses them.</summary>
public readonly record struct Access(TenantContext? Caller, IResult? Failure);

/// <summary>
/// The permission check of the company API (spec 0005 → General rules; spec 0015 → Company API). The token proves who the caller is and in
/// which company it was issued (its <c>org_id</c>); what they may do there is read from the database on every call: their membership, company
/// and role in the token's company at this moment. The space a sign-in would land in now plays no part.
/// <list type="bullet">
/// <item>The token's space is personal (spec 0010): <c>403 forbidden</c>, before the rules below.</item>
/// <item>The database grants the permission: the call proceeds, whatever the token says.</item>
/// <item>It does not (or the caller is no member of the token's company any more), but the token claims it: the caller was demoted or
/// removed since the token was issued, and the answer is <c>403 permissions_changed</c>, so that the frontend refreshes and redraws.</item>
/// <item>Neither grants it: <c>403 forbidden</c>.</item>
/// </list>
/// </summary>
public static class CompanyAccess
{
    /// <param name="anyOf">The call needs one of these permissions; none means any member of a company will do.</param>
    public static Task<Access> AuthorizeAsync(HttpContext http, MembershipReader memberships, params string[] anyOf) =>
        AuthorizeCoreAsync(http, memberships, allowPersonal: false, anyOf);

    /// <summary>
    /// The check of <c>GET /auth/me</c>: any member of any space, a private account included (spec 0010 → The company API). Every endpoint under
    /// <c>/auth/org</c> uses <see cref="AuthorizeAsync"/> instead.
    /// </summary>
    public static Task<Access> AuthorizeAnyKindAsync(HttpContext http, MembershipReader memberships) =>
        AuthorizeCoreAsync(http, memberships, allowPersonal: true, []);

    private static async Task<Access> AuthorizeCoreAsync(HttpContext http, MembershipReader memberships, bool allowPersonal, string[] anyOf)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);

        // The sub of a token this service issued is always a user id; anything else is not a caller we know.
        if (!Guid.TryParse(http.User.FindFirst(Claims.Subject)?.Value, out var userId))
        {
            return new Access(null, ApiResults.Error(TenancyErrors.Forbidden));
        }

        // The company of the token; a token without a usable org_id names none, so the caller has no membership to read.
        var tenant = Guid.TryParse(http.User.FindFirst(TenantClaims.OrgId)?.Value, out var orgId)
            ? await memberships.ReadInCompanyAsync(userId, orgId, http.RequestAborted)
            : null;

        // A personal space is no company (spec 0010). Read from the database, whatever the token says, and also where no permission is needed:
        // a check on permissions alone would let a private account through GET /auth/org.
        if (tenant is { Kind: CompanyKind.Personal } && !allowPersonal)
        {
            return new Access(null, ApiResults.Error(TenancyErrors.Forbidden));
        }

        if (tenant is not null && (anyOf.Length == 0 || anyOf.Any(tenant.Holds)))
        {
            return new Access(tenant, null);
        }

        // Refused. If the token says the caller could, the token is out of date; if it never said so, it is simply no.
        var claimed = anyOf.Length == 0
            ? http.User.HasClaim(c => c.Type == TenantClaims.OrgId)
            : http.User.FindAll(TenantClaims.Permissions).Any(c => anyOf.Contains(c.Value, StringComparer.Ordinal));
        return new Access(null, ApiResults.Error(claimed ? TenancyErrors.PermissionsChanged : TenancyErrors.Forbidden));
    }
}
