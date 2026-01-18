using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;

namespace Auth.Server.Login;

/// <summary>
/// Cuts a successful token response down to the spec's contract, <c>{"status":"authenticated","access_token":"…"}</c>:
/// OpenIddict's <c>token_type</c>, <c>expires_in</c> and anything else it adds are dropped. Error responses (the
/// <c>400</c>s of <see cref="JsonLoginRequestHandler"/>) pass through untouched.
/// </summary>
public sealed class LoginResponseShaper : IOpenIddictServerHandler<OpenIddictServerEvents.ApplyTokenResponseContext>
{
    public const string StatusParameter = "status";
    public const string AuthenticatedStatus = "authenticated";
    private const string AccessTokenParameter = "access_token";

    /// <summary>
    /// Runs just before OpenIddict's ASP.NET Core JSON writer for the token response, so the writer serialises
    /// the reshaped response.
    /// </summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ApplyTokenResponseContext>()
            .UseSingletonHandler<LoginResponseShaper>()
            .SetOrder(OpenIddictServerAspNetCoreHandlers.ProcessJsonResponse<OpenIddictServerEvents.ApplyTokenResponseContext>.Descriptor.Order - 1000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <summary>
    /// The response is shaped only for a password grant on the login path; any other token-endpoint response
    /// (the refresh, which <see cref="Auth.Server.Sessions.SessionResponseHandler"/> shapes itself) passes through
    /// untouched.
    /// </summary>
    internal static bool ShouldShape(OpenIddictRequest? request, PathString path) =>
        request is not null
        && request.IsPasswordGrantType()
        && JsonLoginRequestHandler.IsLoginPath(path);

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ApplyTokenResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Transaction.GetHttpRequest()?.Path ?? default;
        if (!ShouldShape(context.Request, path))
        {
            return ValueTask.CompletedTask;
        }

        if (!string.IsNullOrEmpty(context.Response.Error))
        {
            return ValueTask.CompletedTask;
        }

        var accessToken = context.Response.AccessToken
            ?? throw new InvalidOperationException("A successful token response has no access token.");

        // Replace, rather than remove one by one: whatever else OpenIddict adds must not leak into the contract.
        context.Response = new OpenIddictResponse
        {
            [StatusParameter] = AuthenticatedStatus,
            [AccessTokenParameter] = accessToken,
        };

        return ValueTask.CompletedTask;
    }
}
