using System.Globalization;
using System.Security.Claims;
using Auth.Infrastructure.Identity;
using Auth.Server.Sessions;
using Auth.Server.Tokens;
using Microsoft.AspNetCore;
using TokenOptions = Auth.Server.Tokens.TokenOptions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Login;

/// <summary>
/// The pass-through half of <c>POST /auth/login</c>. By the time it runs, OpenIddict has already parsed the JSON body
/// (see <see cref="JsonLoginRequestHandler"/>) into a password-grant request. This endpoint checks the credentials
/// with ASP.NET Core Identity and either asks OpenIddict to issue the token (<see cref="Results.SignIn"/>) or returns
/// the uniform <c>401 invalid_credentials</c>. The success body is reshaped by <see cref="LoginResponseShaper"/>.
/// </summary>
public static class LoginEndpoint
{
    public const string InvalidCredentialsError = "invalid_credentials";

    public static async Task<IResult> HandleAsync(
        HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The login endpoint was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");

        // Deliberately no lockout, no SignInManager and no timing equalisation: all of that is spec 0003.
        var user = await users.FindByEmailAsync(request.Username ?? string.Empty);
        if (user is null || !await users.CheckPasswordAsync(user, request.Password ?? string.Empty))
        {
            return new InvalidCredentialsResult();
        }

        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        // The token carries `sub` and nothing else: no email, name, role or scope.
        identity.SetClaim(Claims.Subject, user.Id.ToString());
        identity.SetClaim(SessionPolicy.StartClaim, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        var principal = new ClaimsPrincipal(identity);
        principal.SetResources(tokens.Value.Audience);
        // `sub` is the whole access token; the session start stays in the refresh token only.
        principal.SetDestinations(static claim => claim.Type == Claims.Subject ? [Destinations.AccessToken] : []);
        principal.SetRefreshTokenLifetime(SessionPolicy.SlidingLifetime);
        http.Items[RefreshCookie.LifetimeItemKey] = SessionPolicy.SlidingLifetime;

        return Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// The single failure result for unknown email and wrong password alike, so that status, body and headers are
    /// identical. It carries the same cache headers OpenIddict puts on a token response, so it is not
    /// distinguishable from one by caching behaviour either.
    /// </summary>
    private sealed class InvalidCredentialsResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            httpContext.Response.Headers[HeaderNames.CacheControl] = "no-store";
            httpContext.Response.Headers[HeaderNames.Pragma] = "no-cache";
            return Results.Json(new { error = InvalidCredentialsError }, statusCode: StatusCodes.Status401Unauthorized)
                .ExecuteAsync(httpContext);
        }
    }
}
