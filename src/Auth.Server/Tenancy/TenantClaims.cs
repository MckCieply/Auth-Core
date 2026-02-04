using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Tenancy;

/// <summary>
/// The three claims the access token gains (spec 0005 → Access token): <c>org_id</c>, <c>roles</c> and
/// <c>permissions</c>. Login and refresh build them in the same place, from the same read, so they cannot differ.
/// </summary>
public static class TenantClaims
{
    public const string OrgId = "org_id";
    public const string Roles = "roles";
    public const string Permissions = "permissions";

    public static readonly IReadOnlyList<string> Types = [OrgId, Roles, Permissions];

    /// <summary>
    /// Puts the three claims on the identity, replacing any that are there: a refresh starts from the claims of the
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
        identity.AddClaim(new Claim(Roles, JsonSerializer.Serialize(new[] { tenant.RoleName }), JsonClaimValueTypes.JsonArray));
        identity.AddClaim(new Claim(Permissions, JsonSerializer.Serialize(tenant.Permissions), JsonClaimValueTypes.JsonArray));
    }

    /// <summary>
    /// The access token carries <c>sub</c> and the three claims; everything else (the session start and stamp) lives in
    /// the refresh token only.
    /// </summary>
    public static IEnumerable<string> DestinationsOf(Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return claim.Type == Claims.Subject || Types.Contains(claim.Type) ? [Destinations.AccessToken] : [];
    }
}
