using System.Globalization;
using System.Security.Claims;
using Auth.Infrastructure.Identity;
using Auth.Server.Tenancy;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Sessions;

/// <summary>
/// The principal of a new session, which <c>POST /auth/login</c> and <c>POST /auth/signup/complete</c> hand to OpenIddict to sign in. One place
/// builds it, so that the two sign-ins cannot differ: the subject, the start of the session, the security stamp, the company with the role and
/// what it grants, the audience, the destinations, and the lifetime of the refresh token (which the cookie lives for as well).
/// </summary>
public static class SessionPrincipal
{
    public static ClaimsPrincipal Create(HttpContext http, ApplicationUser user, TenantContext tenant, string audience, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audience);
        ArgumentNullException.ThrowIfNull(clock);

        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        // The access token carries `sub` and the tenancy claims below (`org_id`, `org_kind`, `roles`, `permissions`), and nothing else:
        // no email, name or scope.
        identity.SetClaim(Claims.Subject, user.Id.ToString());
        identity.SetClaim(SessionPolicy.StartClaim, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        // The stamp of the account as this sign-in read it. A session that began before a password change, even one
        // whose sign-in was still in flight when the change was committed, carries the old stamp and cannot refresh.
        identity.SetClaim(SessionPolicy.StampClaim, user.SecurityStamp);

        // The company, the role and what it grants, read from the database now.
        TenantClaims.Apply(identity, tenant);

        var principal = new ClaimsPrincipal(identity);
        principal.SetResources(audience);
        // `sub` and the tenancy claims make the access token; the session start stays in the refresh token only.
        principal.SetDestinations(TenantClaims.DestinationsOf);
        principal.SetRefreshTokenLifetime(SessionPolicy.SlidingLifetime);
        http.Items[RefreshCookie.LifetimeItemKey] = SessionPolicy.SlidingLifetime;
        return principal;
    }
}
