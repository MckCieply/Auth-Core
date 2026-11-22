using System.Globalization;
using Auth.Server.Tenancy;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Api;

/// <summary>
/// <c>GET /auth/orgs</c> (spec 0015 → Company list): every space the caller belongs to, a company or their personal one, read from the database
/// now, and which of them the token is for. It needs the access token and nothing else of the company API: it answers also when the token's
/// own company is gone, which is how a client learns that it must switch.
/// </summary>
public static class OrgListEndpoint
{
    public const string Path = "/auth/orgs";

    public static async Task<IResult> ListAsync(HttpContext http, MembershipReader memberships)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);

        // The sub of a token this service issued is always a user id; anything else is not a caller we know (as CompanyAccess has it).
        if (!Guid.TryParse(http.User.FindFirst(Claims.Subject)?.Value, out var userId))
        {
            return ApiResults.Error(TenancyErrors.Forbidden);
        }

        var spaces = await memberships.ListAsync(userId, http.RequestAborted);
        if (spaces.Count == 0)
        {
            // The account is gone, or has no space any more: the token no longer matches the database.
            return ApiResults.Error(TenancyErrors.PermissionsChanged);
        }

        // A token without a usable org_id names no company: no entry is current.
        _ = Guid.TryParse(http.User.FindFirst(TenantClaims.OrgId)?.Value, out var current);
        var items = spaces
            .OrderBy(space => space.CompanyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(space => space.CompanyId.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .Select(space => new OrgListItem(
                space.CompanyId, space.CompanyName, SpaceKinds.NameOf(space.Kind), new RoleRef(space.RoleId, space.RoleName), space.CompanyId == current));
        return ApiResults.Ok(new OrgListResponse([.. items]));
    }
}
