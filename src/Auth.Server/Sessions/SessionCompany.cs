using System.Globalization;
using System.Security.Claims;
using Auth.Server.Tenancy;
using OpenIddict.Abstractions;

namespace Auth.Server.Sessions;

/// <summary>
/// The space a session signed in to (spec 0015 → Session company): its id and when the account joined it, as whole Unix microseconds. The pair
/// names one membership, and a membership that was removed and made again is another one (a later <c>JoinedAt</c>), so a session of the old one
/// does not carry over to it.
/// </summary>
public readonly record struct SessionSpace(Guid CompanyId, long JoinedMicros);

/// <summary>
/// Puts the session's space on a principal and reads it back: the two claims <see cref="SessionPolicy.CompanyClaim"/> and
/// <see cref="SessionPolicy.JoinedClaim"/>. Neither has a destination, so they live in the refresh token only, and neither is a tenancy claim.
/// </summary>
public static class SessionCompany
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMicrosecond;

    /// <summary>The whole Unix microseconds of the UTC instant, rounded down: the precision PostgreSQL keeps.</summary>
    public static long MicrosOf(DateTimeOffset instant)
    {
        var ticks = instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var micros = ticks / TicksPerMicrosecond;
        return ticks % TicksPerMicrosecond < 0 ? micros - 1 : micros;
    }

    /// <summary>Sets both claims from the tenant, replacing any that are present (a refresh starts from the claims of the old token).</summary>
    public static void Apply(ClaimsIdentity identity, TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        identity.SetClaim(SessionPolicy.CompanyClaim, tenant.CompanyId.ToString());
        identity.SetClaim(SessionPolicy.JoinedClaim, MicrosOf(tenant.JoinedAt).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The session's space; <see langword="null"/> when either claim is missing or does not parse.</summary>
    public static SessionSpace? Read(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return Guid.TryParse(principal.GetClaim(SessionPolicy.CompanyClaim), out var company)
            && long.TryParse(principal.GetClaim(SessionPolicy.JoinedClaim), NumberStyles.Integer, CultureInfo.InvariantCulture, out var joined)
                ? new SessionSpace(company, joined)
                : null;
    }
}
