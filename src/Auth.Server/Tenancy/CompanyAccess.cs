using Auth.Server.Api;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Tenancy;

/// <summary>The caller of a company API endpoint, or the answer that refuses them.</summary>
public readonly record struct Access(TenantContext? Caller, IResult? Failure);

/// <summary>
/// The permission check of the company API (spec 0005 → General rules). The token proves who the caller is; what they
/// may do is read from the database on every call: their membership, company and role at this moment.
/// <list type="bullet">
/// <item>The database grants the permission: the call proceeds, whatever the token says.</item>
/// <item>It does not, but the token claims it: the caller was demoted or removed since the token was issued, and
/// the answer is <c>403 permissions_changed</c>, so that the frontend refreshes and redraws.</item>
/// <item>Neither grants it: <c>403 forbidden</c>.</item>
/// </list>
/// </summary>
public static class CompanyAccess
{
    /// <param name="anyOf">The call needs one of these permissions; none means any member will do.</param>
    public static async Task<Access> AuthorizeAsync(HttpContext http, MembershipReader memberships, params string[] anyOf)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);

        // The sub of a token this service issued is always a user id; anything else is not a caller we know.
        if (!Guid.TryParse(http.User.FindFirst(Claims.Subject)?.Value, out var userId))
        {
            return new Access(null, ApiResults.Error(TenancyErrors.Forbidden));
        }

        var tenant = await memberships.ReadAsync(userId, http.RequestAborted);
        if (tenant is not null && (anyOf.Length == 0 || anyOf.Any(tenant.Holds)))
        {
            return new Access(tenant, null);
        }

        // Refused. If the token says the caller could, the token is out of date; if it never said so, it is simply no.
        var claimed = anyOf.Length == 0
            ? http.User.HasClaim(c => c.Type == TenantClaims.OrgId)
            : http.User.FindAll(TenantClaims.Permissions).Any(c => anyOf.Contains(c.Value, StringComparer.Ordinal));
        return new Access(null, ApiResults.Error(claimed ? TenancyErrors.PermissionsChanged : TenancyErrors.Forbidden));
    }
}
