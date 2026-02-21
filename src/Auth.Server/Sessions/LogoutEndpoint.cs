using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
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

    public static async Task<IResult> HandleAsync(
        HttpContext http, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, AuthDbContext db, AuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(authorizations);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);

        if (RefreshCookie.TryRead(http.Request, out var reference))
        {
            // The revocation and the row of the logout are one transaction (spec 0008: a change and its row are written together, or
            // neither is). OpenIddict's stores use the request's AuthDbContext, so their saves join it. If the row cannot be written the
            // logout is a 500 and the session is still there.
            await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
            var subject = await RevokeFamilyAsync(reference, tokens, authorizations, http.RequestAborted);
            if (Guid.TryParse(subject, out var userId))
            {
                // The account the cookie's session belonged to, from the token store; a logout with no valid cookie writes nothing.
                audit.Stage(new AuditEntry { Kind = AuditKinds.Logout, ActorUserId = userId, SubjectUserId = userId });
                await db.SaveChangesAsync(http.RequestAborted);
            }

            await transaction.CommitAsync(http.RequestAborted);
        }

        RefreshCookie.Clear(http.Response);
        http.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return Results.NoContent();
    }

    /// <summary>
    /// Revokes the authorization the token belongs to, and every token under it. A consumed token of the family
    /// counts too: a stale tab logging out must still end the session. Returns the account of the session when the token was a
    /// refresh token that had not been revoked yet, so that the logout can be recorded; <see langword="null"/> otherwise.
    /// </summary>
    private static async Task<string?> RevokeFamilyAsync(
        string reference, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, CancellationToken cancellationToken)
    {
        var token = await tokens.FindByReferenceIdAsync(reference, cancellationToken);
        if (token is null || !await tokens.HasTypeAsync(token, TokenTypeIdentifiers.RefreshToken, cancellationToken))
        {
            return null;
        }

        var subject = await tokens.HasStatusAsync(token, Statuses.Revoked, cancellationToken)
            ? null
            : await tokens.GetSubjectAsync(token, cancellationToken);
        var family = await tokens.GetAuthorizationIdAsync(token, cancellationToken);
        if (string.IsNullOrEmpty(family))
        {
            await tokens.TryRevokeAsync(token, cancellationToken);
            return subject;
        }

        if (await authorizations.FindByIdAsync(family, cancellationToken) is { } authorization)
        {
            await authorizations.TryRevokeAsync(authorization, cancellationToken);
        }

        await tokens.RevokeByAuthorizationIdAsync(family, cancellationToken);
        return subject;
    }
}
