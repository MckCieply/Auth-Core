using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Tenancy;

/// <summary>
/// The tenancy claims of the access token: <c>org_id</c>, <c>roles</c> and <c>permissions</c> (spec 0005 → Access token) and
/// <c>org_kind</c> (spec 0010). Login, refresh and sign-up completion build them in the same place, from the same read, so they cannot differ.
/// </summary>
public static class TenantClaims
{
    public const string OrgId = "org_id";
    public const string OrgKind = "org_kind";
    public const string Roles = "roles";
    public const string Permissions = "permissions";

    public static readonly IReadOnlyList<string> Types = [OrgId, OrgKind, Roles, Permissions];

    /// <summary>
    /// Puts the tenancy claims on the identity, replacing any that are there: a refresh starts from the claims of the
    /// refresh token, and what they say is out of date. The two lists are JSON arrays, so that a role list of one
    /// name is an array in the token and not a string.
    /// </summary>
    public static void Apply(ClaimsIdentity identity, TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        foreach (var stale in identity.Claims.Where(c => Types.Contains(c.Type)).ToList())
        {
            identity.RemoveClaim(stale);
        }

        identity.AddClaim(new Claim(OrgId, tenant.CompanyId.ToString(), ClaimValueTypes.String));
        identity.AddClaim(new Claim(OrgKind, SpaceKinds.NameOf(tenant.Kind), ClaimValueTypes.String));
        identity.AddClaim(new Claim(Roles, JsonSerializer.Serialize(new[] { tenant.RoleName }), JsonClaimValueTypes.JsonArray));
        identity.AddClaim(new Claim(Permissions, JsonSerializer.Serialize(tenant.Permissions), JsonClaimValueTypes.JsonArray));
    }

    /// <summary>
    /// The access token carries <c>sub</c> and the tenancy claims; everything else (the session start and stamp) lives in
    /// the refresh token only.
    /// </summary>
    public static IEnumerable<string> DestinationsOf(Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return claim.Type == Claims.Subject || Types.Contains(claim.Type) ? [Destinations.AccessToken] : [];
    }
}
