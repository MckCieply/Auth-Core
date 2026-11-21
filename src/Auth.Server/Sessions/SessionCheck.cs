using System.Security.Claims;
using Auth.Server.Tenancy;

namespace Auth.Server.Sessions;

/// <summary>What the checks of a refresh found: either <see cref="Failure"/>, or the authenticated principal with what the next pair is made from.</summary>
/// <param name="Failure">The answer to give (the refresh's <c>invalid_grant</c>); <see langword="null"/> when every check passed.</param>
/// <param name="Principal">The principal OpenIddict authenticated from the refresh token; set when there is no failure.</param>
/// <param name="UserId">The account the token belongs to.</param>
/// <param name="Lifetime">The lifetime of the next refresh token (the sliding window, cut at the session's cap).</param>
/// <param name="Space">The membership of the session's company as the database says now.</param>
internal sealed record SessionCheck(IResult? Failure, ClaimsPrincipal? Principal, Guid UserId, TimeSpan Lifetime, TenantContext? Space)
{
    internal static SessionCheck Failed() => new(RefreshEndpoint.InvalidGrant(), null, Guid.Empty, TimeSpan.Zero, null);
}
