using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Sessions;

/// <summary>
/// Keeps the access token's claim set as spec 0001 pinned it. Once a sign-in has a refresh token, OpenIddict creates
/// an authorization (the token family) and stamps its id into the principal as <c>oi_au_id</c>, so it would end up in
/// the access token, which is not part of the contract (spec 0002, Decision 16). The claim is removed from the access
/// token only: the refresh token keeps it, and the stored token entry is already linked to the authorization.
/// </summary>
public sealed class AccessTokenClaimFilter : IOpenIddictServerHandler<OpenIddictServerEvents.GenerateTokenContext>
{
    /// <summary>
    /// After <c>AttachTokenMetadata</c>, so that the claims OpenIddict copied from the principal into the token
    /// descriptor are all there, and before <c>GenerateIdentityModelToken</c> signs them into the JWT.
    /// </summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.GenerateTokenContext>()
            .UseSingletonHandler<AccessTokenClaimFilter>()
            .SetOrder(OpenIddictServerHandlers.Protection.AttachTokenMetadata.Descriptor.Order + 500)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The descriptor's subject is a copy of the principal's claims: removing from the principal itself would be too late.
        if (context.TokenType == TokenTypeIdentifiers.AccessToken && context.SecurityTokenDescriptor?.Subject is { } subject)
        {
            foreach (var claim in subject.FindAll(Claims.Private.AuthorizationId).ToList())
            {
                subject.RemoveClaim(claim);
            }
        }

        return ValueTask.CompletedTask;
    }
}
