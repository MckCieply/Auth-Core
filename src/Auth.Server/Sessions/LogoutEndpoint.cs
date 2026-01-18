using Microsoft.Net.Http.Headers;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Sessions;

/// <summary>
/// <c>POST /auth/logout</c>: ends the session the cookie belongs to and clears the cookie. Always <c>204</c>, with
/// or without a valid cookie, so the answer says nothing about whether a session existed.
/// </summary>
/// <remarks>
/// An access token already issued stays valid until it expires (at most 10 minutes): it is verified offline and
/// cannot be recalled (spec 0002, Decision 14).
/// </remarks>
public static class LogoutEndpoint
{
    public const string LogoutPath = "/auth/logout";

    public static async Task<IResult> HandleAsync(HttpContext http, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(authorizations);

        if (RefreshCookie.TryRead(http.Request, out var reference))
        {
            await RevokeFamilyAsync(reference, tokens, authorizations, http.RequestAborted);
        }

        RefreshCookie.Clear(http.Response);
        http.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return Results.NoContent();
    }

    /// <summary>
    /// Revokes the authorization the token belongs to, and every token under it. A consumed token of the family
    /// counts too: a stale tab logging out must still end the session.
    /// </summary>
    private static async Task RevokeFamilyAsync(
        string reference, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, CancellationToken cancellationToken)
    {
        var token = await tokens.FindByReferenceIdAsync(reference, cancellationToken);
        if (token is null || !await tokens.HasTypeAsync(token, TokenTypeIdentifiers.RefreshToken, cancellationToken))
        {
            return;
        }

        var family = await tokens.GetAuthorizationIdAsync(token, cancellationToken);
        if (string.IsNullOrEmpty(family))
        {
            await tokens.TryRevokeAsync(token, cancellationToken);
            return;
        }

        if (await authorizations.FindByIdAsync(family, cancellationToken) is { } authorization)
        {
            await authorizations.TryRevokeAsync(authorization, cancellationToken);
        }

        await tokens.RevokeByAuthorizationIdAsync(family, cancellationToken);
    }
}
