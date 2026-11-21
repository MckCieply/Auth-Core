using System.Globalization;
using System.Security.Claims;
using Auth.Infrastructure.Identity;
using Auth.Server.Login;
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
/// user still exists and is still a member of the session's company (spec 0015), and asks OpenIddict to issue the next pair of tokens. The tokens are written in a
/// transaction that begins by reading the user again under a share lock (<see cref="AtomicSignInResult"/>): a user deleted in the meantime
/// gets <c>invalid_grant</c> and no token entry is written for them.
/// </summary>
public static class RefreshEndpoint
{
    public static async Task<IResult> HandleAsync(
        HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens, TimeProvider clock, MembershipReader memberships)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var check = await ReadSessionAsync(http, users, clock, memberships);
        if (check.Failure is { } failure)
        {
            return failure;
        }

        // Start from the refresh token's own claims, so OpenIddict's internal ones (the authorization id that makes
        // the family) carry over to the new tokens.
        var identity = new ClaimsIdentity(
            check.Principal!.Claims,
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);
        identity.SetClaim(Claims.Subject, check.UserId.ToString());
        TenantClaims.Apply(identity, check.Space!);

        // Overwrites the session claims copied from the old token; a token of before spec 0015 gets them here.
        SessionCompany.Apply(identity, check.Space!);

        var principal = new ClaimsPrincipal(identity);
        principal.SetResources(tokens.Value.Audience);
        principal.SetDestinations(TenantClaims.DestinationsOf);
        principal.SetRefreshTokenLifetime(check.Lifetime);
        http.Items[RefreshCookie.LifetimeItemKey] = check.Lifetime;

        // A refresh holds the session's membership (it never writes LastUsedAt); a membership that is gone or another one is invalid_grant.
        return new AtomicSignInResult(principal, entry: null, whenGone: () => Task.FromResult(InvalidGrant()), use: SpaceUse.Hold);
    }

    /// <summary>
    /// The checks of a refresh (also the switch's, spec 0015): the account is there, its security stamp is the token's, the session is within
    /// its cap, and the membership in the session's company is there and is the one the session names (the same <c>JoinedAt</c> in whole
    /// microseconds, both sides read from the database). A token of the code before spec 0015 has no session company and takes the
    /// sign-in's space; every other failure is the refresh's <c>invalid_grant</c>.
    /// </summary>
    internal static async Task<SessionCheck> ReadSessionAsync(
        HttpContext http, UserManager<ApplicationUser> users, TimeProvider clock, MembershipReader memberships)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(memberships);

        var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var subject = result.Principal?.GetClaim(Claims.Subject);

        var user = subject is null ? null : await users.FindByIdAsync(subject);
        if (user is null)
        {
            return SessionCheck.Failed();
        }

        // A password change ends every session (spec 0004): it revokes the tokens it can see, and it changes the
        // account's security stamp, which also stops a session whose login overlapped the change.
        if (!string.Equals(result.Principal!.GetClaim(SessionPolicy.StampClaim), user.SecurityStamp, StringComparison.Ordinal))
        {
            return SessionCheck.Failed();
        }

        // A refresh token without a readable session start cannot be placed against the cap: reject it.
        if (!long.TryParse(result.Principal!.GetClaim(SessionPolicy.StartClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var startSeconds)
            || SessionPolicy.RemainingLifetime(DateTimeOffset.FromUnixTimeSeconds(startSeconds), clock.GetUtcNow()) is not { } lifetime)
        {
            return SessionCheck.Failed();
        }

        // Someone who is no longer a member of the session's company has no session (spec 0005 → Effects of removing a member, spec 0015 →
        // Refresh), and everyone else gets the company, its kind, role and permissions as the database says now: a role change reaches the next
        // token here. A membership removed and made again is another one (its JoinedAt differs). A token of before spec 0015 names no company:
        // it takes the sign-in's space, and the next token carries it.
        // A token with only one of the two claims, or one that does not read, is no token of before spec 0015: it is refused.
        var named = SessionCompany.Read(result.Principal!);
        if (named is null && SessionCompany.HasAny(result.Principal!))
        {
            return SessionCheck.Failed();
        }

        var tenant = named is { } session
            ? await memberships.ReadInCompanyAsync(user.Id, session.CompanyId, http.RequestAborted)
            : await memberships.ReadSignInSpaceAsync(user.Id, http.RequestAborted);
        if (tenant is null || (named is { } expected && SessionCompany.MicrosOf(tenant.JoinedAt) != expected.JoinedMicros))
        {
            return SessionCheck.Failed();
        }

        return new SessionCheck(null, result.Principal, user.Id, lifetime, tenant);
    }

    /// <summary>Rejects through OpenIddict, so the failure takes the same path (and shape) as its own rejections.</summary>
    internal static IResult InvalidGrant() => Results.Forbid(
        properties: new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
        }),
        authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
