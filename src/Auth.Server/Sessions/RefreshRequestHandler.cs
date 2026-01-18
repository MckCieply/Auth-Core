using Auth.Server.Login;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Sessions;

/// <summary>
/// Turns <c>POST /auth/refresh</c> into an OpenIddict refresh-token request. The <c>auth_rt</c> cookie is the only
/// input: the body is never read, so it can neither supply a token nor switch the grant.
/// </summary>
public sealed class RefreshRequestHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>
{
    public const string RefreshPath = "/auth/refresh";

    /// <summary>
    /// <see langword="true"/> for <c>/auth/refresh</c> and <c>/auth/refresh/</c>, case-insensitive: OpenIddict
    /// matches the token endpoint with an optional trailing slash, so this check must agree with it.
    /// </summary>
    internal static bool IsRefreshPath(PathString path) =>
        path.Equals(RefreshPath, StringComparison.OrdinalIgnoreCase)
        || path.Equals(RefreshPath + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>After <see cref="JsonLoginRequestHandler"/>, before <see cref="UnhandledTokenRequestGuard"/>.</summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ExtractTokenRequestContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<RefreshRequestHandler>()
            .SetOrder(JsonLoginRequestHandler.Descriptor.Order + 250)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Transaction.GetHttpRequest()
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        if (!IsRefreshPath(request.Path))
        {
            return ValueTask.CompletedTask;
        }

        if (!HttpMethods.IsPost(request.Method))
        {
            context.Reject(error: Errors.InvalidRequest, description: "The refresh endpoint only accepts POST requests.");
            return ValueTask.CompletedTask;
        }

        if (!RefreshCookie.TryRead(request, out var token))
        {
            context.Reject(error: Errors.InvalidGrant);
            return ValueTask.CompletedTask;
        }

        context.Request = new OpenIddictRequest
        {
            GrantType = GrantTypes.RefreshToken,
            RefreshToken = token,
        };

        return ValueTask.CompletedTask;
    }
}
