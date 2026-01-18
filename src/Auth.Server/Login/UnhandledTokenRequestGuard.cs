using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Login;

/// <summary>
/// Defence in depth for <see cref="JsonLoginRequestHandler"/>. OpenIddict's own form extraction is removed from the
/// token endpoint, so a token-endpoint request that no extraction handler claims would reach OpenIddict's following
/// handlers with a <see langword="null"/> request and crash with a <c>500</c>. This handler turns that case into a
/// <c>400 invalid_request</c> instead. Every flow's extraction handler (<see cref="Auth.Server.Sessions.RefreshRequestHandler"/>
/// for <c>/auth/refresh</c>) must be ordered before it.
/// </summary>
public sealed class UnhandledTokenRequestGuard : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>
{
    /// <summary>
    /// Runs after <see cref="JsonLoginRequestHandler"/> (which holds the order of OpenIddict's
    /// <c>ExtractPostRequest</c>) and before <c>ValidateClientAuthenticationMethod</c>, the first built-in handler that
    /// dereferences the request (order of <c>ExtractPostRequest</c> + 1000).
    /// </summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ExtractTokenRequestContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<UnhandledTokenRequestGuard>()
            .SetOrder(JsonLoginRequestHandler.Descriptor.Order + 500)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsRejected && context.Request is null)
        {
            context.Reject(error: Errors.InvalidRequest, description: "The request is not supported by this endpoint.");
        }

        return ValueTask.CompletedTask;
    }
}
