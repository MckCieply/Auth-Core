using System.Globalization;
using System.Security.Claims;
using Auth.Infrastructure.Identity;
using Auth.Server.Account;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Auth.Server.Tokens;
using Microsoft.AspNetCore;
using TokenOptions = Auth.Server.Tokens.TokenOptions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Login;

/// <summary>
/// The pass-through half of <c>POST /auth/login</c>. By the time it runs, OpenIddict has already parsed the JSON body
/// (see <see cref="JsonLoginRequestHandler"/>) into a password-grant request. This endpoint checks the credentials
/// with ASP.NET Core Identity and either asks OpenIddict to issue the token (<see cref="Results.SignIn"/>) or returns
/// the uniform <c>401 invalid_credentials</c>. Every attempt is counted first (<see cref="LoginStreakStore"/>); during
/// a cooldown it is refused with <c>429</c> before the account is looked up or the password evaluated. The success body is reshaped by <see cref="LoginResponseShaper"/>.
/// A correct password for an account whose email is not confirmed is refused with <c>403 email_not_verified</c>, and
/// one for an account that belongs to no company with <c>403 no_membership</c>; an access token carries the company,
/// the role and its permissions as the database says at that moment (spec 0005).
/// </summary>
public static class LoginEndpoint
{
    public const string InvalidCredentialsError = "invalid_credentials";

    public static async Task<IResult> HandleAsync(
        HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens, TimeProvider clock, LoginStreakStore streaks,
        DecoyPasswordHash decoy, MembershipReader memberships, AuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(decoy);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(audit);

        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The login endpoint was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");

        // Count the attempt before anything about it is known (spec 0003, Decision 14). While a cooldown runs
        // there is no lookup and no password check: the response must not depend on either (Decision 7).
        var identifier = LoginIdentifier.HashOf(users.NormalizeEmail(request.Username));
        var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
        if (!decision.Allowed)
        {
            // Nothing is looked up while a cooldown runs, so the row names the address as typed (spec 0008 -> Audit log).
            await audit.WriteAloneAsync(new AuditEntry
            {
                Kind = AuditKinds.LoginLocked,
                SubjectEmail = request.Username,
                Details = new Dictionary<string, object?> { ["retry_after_seconds"] = TooManyAttemptsResult.SecondsOf(decision.RetryAfter) },
            });
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        var password = request.Password ?? string.Empty;
        var user = await users.FindByEmailAsync(request.Username ?? string.Empty);
        if (user?.PasswordHash is null)
        {
            // No account, or one without a password: still pay for one verification, so this answer takes as long
            // as a wrong password does.
            _ = users.PasswordHasher.VerifyHashedPassword(user ?? new ApplicationUser(), decoy.Value, password);
            await RecordFailureAsync(audit, "unknown_address", null, request.Username ?? string.Empty);
            return new InvalidCredentialsResult();
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await RecordFailureAsync(audit, "wrong_password", user, request.Username ?? string.Empty);
            return new InvalidCredentialsResult();
        }

        // Not the request's token: from the tenth attempt on, counting has already started a cooldown, and a client
        // that goes away right after its correct password was verified must not be left with it.
        await streaks.ClearAsync(identifier, CancellationToken.None);

        // The password is proven, so the streak is over either way. But an account whose email is not confirmed
        // gets no session (spec 0004, Decision 3). Only someone who knows the password can see this answer.
        if (!user.EmailConfirmed)
        {
            await RecordFailureAsync(audit, "unconfirmed_address", user, request.Username ?? string.Empty);
            return AccountResults.EmailNotVerified();
        }

        // Likewise an account that belongs to no company (spec 0005): it has nothing to put into a token.
        if (await memberships.ReadAsync(user.Id, http.RequestAborted) is not { } tenant)
        {
            await RecordFailureAsync(audit, "no_company", user, request.Username ?? string.Empty);
            return AccountResults.NoMembership();
        }

        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        // The access token carries `sub` and the tenancy claims below (`org_id`, `roles`, `permissions`), and nothing else:
        // no email, name or scope.
        identity.SetClaim(Claims.Subject, user.Id.ToString());
        identity.SetClaim(SessionPolicy.StartClaim, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        // The stamp of the account as this login read it. A session that began before a password change, even one
        // whose login was still in flight when the change was committed, carries the old stamp and cannot refresh.
        identity.SetClaim(SessionPolicy.StampClaim, user.SecurityStamp);

        // The company, the role and what it grants, read from the database now.
        TenantClaims.Apply(identity, tenant);

        var principal = new ClaimsPrincipal(identity);
        principal.SetResources(tokens.Value.Audience);
        // `sub` and the tenancy claims make the access token; the session start stays in the refresh token only.
        principal.SetDestinations(TenantClaims.DestinationsOf);
        principal.SetRefreshTokenLifetime(SessionPolicy.SlidingLifetime);
        http.Items[RefreshCookie.LifetimeItemKey] = SessionPolicy.SlidingLifetime;

        // The session and its row are one change: AtomicSignInResult issues the one inside a transaction that also holds the other.
        return new AtomicSignInResult(principal, new AuditEntry
        {
            Kind = AuditKinds.LoginSucceeded,
            ActorUserId = user.Id,
            SubjectUserId = user.Id,
            SubjectEmail = user.Email,
            OrgId = tenant.CompanyId,
            OrgName = tenant.CompanyName,
        });
    }

    /// <summary>
    /// A failed login: the account as stored when there is one, else the address as typed. One insert whatever the reason, so the
    /// answers of an unknown address and of a wrong password stay as slow as each other.
    /// </summary>
    private static Task RecordFailureAsync(AuditLog audit, string reason, ApplicationUser? user, string typedEmail) =>
        audit.WriteAloneAsync(new AuditEntry
        {
            Kind = AuditKinds.LoginFailed,
            SubjectUserId = user?.Id,
            SubjectEmail = user?.Email ?? typedEmail,
            Details = new Dictionary<string, object?> { ["reason"] = reason },
        });

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
