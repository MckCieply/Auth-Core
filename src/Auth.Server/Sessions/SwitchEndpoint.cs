using Auth.Infrastructure.Identity;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Login;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Sessions;

/// <summary>
/// <c>POST /auth/switch</c> (spec 0015): the refresh of the session, for another space of the same account. The body was checked and the cookie
/// turned into a refresh-token request by <see cref="SwitchRequestHandler"/>; here the session is checked as a refresh checks it, then the target
/// is looked up: a space the account does not belong to is <c>404 not_found</c>, answered before anything is signed in, so the token stays unused.
/// Otherwise the one transaction of <see cref="AtomicSignInResult"/> uses the token up as a refresh does, writes the next pair for the target,
/// sets its <c>LastUsedAt</c> and, when the target is not the session's own company, writes <c>org.switched</c>. It holds the session's company
/// while it takes the target, so a session whose company was removed is not moved on (<c>401</c>), and a target removed meanwhile is the <c>404</c>.
/// </summary>
public static class SwitchEndpoint
{
    public static async Task<IResult> SwitchAsync(
        HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens, TimeProvider clock, MembershipReader memberships)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(memberships);

        var check = await RefreshEndpoint.ReadSessionAsync(http, users, clock, memberships);
        if (check.Failure is { } failure)
        {
            return failure;
        }

        // The handler has checked the member; it is read again here, from the request OpenIddict made of it.
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The switch endpoint was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        if (!IdInput.TryParse(request.GetParameter(SwitchRequestHandler.OrgIdParameter)?.ToString(), out var targetId))
        {
            return ApiResults.InvalidRequest();
        }

        // Not a space of the account: nothing is signed in, so the token is not used up (the probe the spec asks for answers the same for
        // a company of others, an id nobody has and another account's personal space).
        var target = await memberships.ReadInCompanyAsync(check.UserId, targetId, http.RequestAborted);
        if (target is null)
        {
            return ApiResults.Error(TenancyErrors.NotFound);
        }

        var session = check.Space!;
        var principal = RefreshEndpoint.NextPrincipal(http, tokens, check, target);

        if (target.CompanyId == session.CompanyId)
        {
            // The session's own company: a refresh that also writes LastUsedAt, and no row. The session's company is the target, so a
            // membership that is gone (or another one) is the refresh's invalid_grant.
            return new AtomicSignInResult(
                principal, entry: null, whenGone: () => Task.FromResult(RefreshEndpoint.InvalidGrant()), use: SpaceUse.Take);
        }

        var entry = new AuditEntry
        {
            Kind = AuditKinds.OrgSwitched,
            ActorUserId = check.UserId,
            SubjectUserId = check.UserId,
            SubjectEmail = check.Email,
            OrgId = target.CompanyId,
            OrgName = target.CompanyName,
            Details = new Dictionary<string, object?> { ["from_org_id"] = session.CompanyId.ToString() },
        };

        // The session's company is held first (gone: invalid_grant), then the target is taken (gone: not_found).
        return new AtomicSignInResult(
            principal,
            entry,
            whenGone: () => Task.FromResult(RefreshEndpoint.InvalidGrant()),
            use: SpaceUse.Take,
            whenSpaceGone: () => Task.FromResult(ApiResults.Error(TenancyErrors.NotFound)),
            alsoHold: new SessionSpace(session.CompanyId, SessionCompany.MicrosOf(session.JoinedAt)));
    }
}
