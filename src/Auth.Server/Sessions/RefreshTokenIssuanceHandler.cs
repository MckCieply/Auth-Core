using OpenIddict.Server;

namespace Auth.Server.Sessions;

/// <summary>
/// Every sign-in on the token endpoint gets a refresh token. OpenIddict would otherwise require the
/// <c>offline_access</c> scope, which would also put a <c>scope</c> claim into the access token and break its
/// pinned claim set (spec 0002, Decision 16).
/// </summary>
public sealed class RefreshTokenIssuanceHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
{
    /// <summary>
    /// After OpenIddict decided which tokens to generate, and before it attaches the ad-hoc authorization, which it
    /// creates only when a refresh token is generated and the principal carries none. That authorization is the token
    /// family. A new session of ours already carries the authorization that <c>AtomicSignInResult</c> made with its
    /// <c>SessionStates</c> row (spec 0012 → Sessions), so for it this handler only decides that a refresh token is made.
    /// </summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
            .UseSingletonHandler<RefreshTokenIssuanceHandler>()
            .SetOrder(OpenIddictServerHandlers.EvaluateGeneratedTokens.Descriptor.Order + 500)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.EndpointType is OpenIddictServerEndpointType.Token)
        {
            context.GenerateRefreshToken = true;
            context.IncludeRefreshToken = true;
        }

        return ValueTask.CompletedTask;
    }
}
