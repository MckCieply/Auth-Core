using Auth.Infrastructure.Identity;
using Auth.Server.Login;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Sessions;

/// <summary>
/// <c>POST /auth/switch</c> (spec 0015), part 1: the path, its body checks, its answers and its limit are the real ones
/// (<see cref="SwitchRequestHandler"/>); the switch itself is still a stub that renews the session in the session's own company and ignores
/// <c>org_id</c>. The target, the <c>404</c> and the audit row come with the next part.
/// </summary>
public static class SwitchEndpoint
{
    public static async Task<IResult> SwitchAsync(
        HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens, TimeProvider clock, MembershipReader memberships)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var check = await RefreshEndpoint.ReadSessionAsync(http, users, clock, memberships);
        if (check.Failure is { } failure)
        {
            return failure;
        }

        var principal = RefreshEndpoint.NextPrincipal(http, tokens, check, check.Space!);

        return new AtomicSignInResult(principal, entry: null, whenGone: () => Task.FromResult(RefreshEndpoint.InvalidGrant()), use: SpaceUse.Hold);
    }
}
