using System.Security.Claims;
using Auth.Infrastructure.Identity;
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
/// user still exists and asks OpenIddict to issue the next pair of tokens.
/// </summary>
public static class RefreshEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tokens);

        var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var subject = result.Principal?.GetClaim(Claims.Subject);

        // Only existence is checked (spec 0002, Decision 11). Ending sessions on a password change is the
        // forgot/reset spec's job.
        var user = subject is null ? null : await users.FindByIdAsync(subject);
        if (user is null)
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

        var principal = new ClaimsPrincipal(identity);
        principal.SetResources(tokens.Value.Audience);
        principal.SetDestinations(static claim => claim.Type == Claims.Subject ? [Destinations.AccessToken] : []);

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
