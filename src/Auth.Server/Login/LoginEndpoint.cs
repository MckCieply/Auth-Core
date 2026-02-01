using System.Globalization;
using System.Security.Claims;
using Auth.Infrastructure.Identity;
using Auth.Server.Account;
using Auth.Server.Lockout;
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
/// the uniform <c>401 invalid_credentials</c>. Every attempt is counted first (<see cref="LoginStreakStore"/>); during
/// a cooldown it is refused with <c>429</c> before the account is looked up or the password evaluated. The success body is reshaped by <see cref="LoginResponseShaper"/>.
/// A correct password for an account whose email is not confirmed is refused with <c>403 email_not_verified</c>.
/// </summary>
public static class LoginEndpoint
{
    public const string InvalidCredentialsError = "invalid_credentials";

    public static async Task<IResult> HandleAsync(
        HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens, TimeProvider clock, LoginStreakStore streaks,
        DecoyPasswordHash decoy)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(decoy);

        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The login endpoint was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");

        // Count the attempt before anything about it is known (spec 0003, Decision 14). While a cooldown runs
        // there is no lookup and no password check: the response must not depend on either (Decision 7).
        var identifier = LoginIdentifier.HashOf(users.NormalizeEmail(request.Username));
        var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
        if (!decision.Allowed)
        {
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        var password = request.Password ?? string.Empty;
        var user = await users.FindByEmailAsync(request.Username ?? string.Empty);
        if (user?.PasswordHash is null)
        {
            // No account, or one without a password: still pay for one verification, so this answer takes as long
            // as a wrong password does.
            _ = users.PasswordHasher.VerifyHashedPassword(user ?? new ApplicationUser(), decoy.Value, password);
            return new InvalidCredentialsResult();
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            return new InvalidCredentialsResult();
        }

        // Not the request's token: from the tenth attempt on, counting has already started a cooldown, and a client
        // that goes away right after its correct password was verified must not be left with it.
        await streaks.ClearAsync(identifier, CancellationToken.None);

        // The password is proven, so the streak is over either way. But an account whose email is not confirmed
        // gets no session (spec 0004, Decision 3). Only someone who knows the password can see this answer.
        if (!user.EmailConfirmed)
        {
            return AccountResults.EmailNotVerified();
        }

        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        // The token carries `sub` and nothing else: no email, name, role or scope.
        identity.SetClaim(Claims.Subject, user.Id.ToString());
        identity.SetClaim(SessionPolicy.StartClaim, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        // The stamp of the account as this login read it. A session that began before a password change, even one
        // whose login was still in flight when the change was committed, carries the old stamp and cannot refresh.
        identity.SetClaim(SessionPolicy.StampClaim, user.SecurityStamp);

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
