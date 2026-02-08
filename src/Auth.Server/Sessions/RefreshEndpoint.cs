using System.Globalization;
using System.Security.Claims;
using Auth.Infrastructure.Identity;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Sessions;

/// <summary>
/// The pass-through half of <c>POST /auth/refresh</c>. By the time it runs, OpenIddict has validated the refresh
/// token from the cookie (unknown, expired, revoked and reused tokens never get here). This endpoint checks that the
/// user still exists and is still a member of a company, and asks OpenIddict to issue the next pair of tokens.
/// </summary>
public static class RefreshEndpoint
{
    public static async Task<IResult> HandleAsync(
        HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens, TimeProvider clock, MembershipReader memberships)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(memberships);

        var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var subject = result.Principal?.GetClaim(Claims.Subject);

        var user = subject is null ? null : await users.FindByIdAsync(subject);
        if (user is null)
        {
            return InvalidGrant();
        }

        // A password change ends every session (spec 0004): it revokes the tokens it can see, and it changes the
        // account's security stamp, which also stops a session whose login overlapped the change.
        if (!string.Equals(result.Principal!.GetClaim(SessionPolicy.StampClaim), user.SecurityStamp, StringComparison.Ordinal))
        {
            return InvalidGrant();
        }

        // A refresh token without a readable session start cannot be placed against the cap: reject it.
        if (!long.TryParse(result.Principal!.GetClaim(SessionPolicy.StartClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var startSeconds)
            || SessionPolicy.RemainingLifetime(DateTimeOffset.FromUnixTimeSeconds(startSeconds), clock.GetUtcNow()) is not { } lifetime)
        {
            return InvalidGrant();
        }

        // Someone who is no longer a member has no session (spec 0005 → Effects of removing a member), and everyone else
        // gets the company, role and permissions as the database says now: a role change reaches the next token here.
        if (await memberships.ReadAsync(user.Id, http.RequestAborted) is not { } tenant)
        {
            return InvalidGrant();
        }

        // Start from the refresh token's own claims, so OpenIddict's internal ones (the authorization id that makes
        // the family) carry over to the new tokens.
        var identity = new ClaimsIdentity(
            result.Principal!.Claims,
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString());
        TenantClaims.Apply(identity, tenant);

        var principal = new ClaimsPrincipal(identity);
        principal.SetResources(tokens.Value.Audience);
        principal.SetDestinations(TenantClaims.DestinationsOf);
        principal.SetRefreshTokenLifetime(lifetime);
        http.Items[RefreshCookie.LifetimeItemKey] = lifetime;

        return Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>Rejects through OpenIddict, so the failure takes the same path (and shape) as its own rejections.</summary>
    internal static IResult InvalidGrant() => Results.Forbid(
        properties: new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
        }),
        authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
