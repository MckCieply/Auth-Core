using Auth.Server.Login;
using Microsoft.AspNetCore;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;

namespace Auth.Server.Sessions;

/// <summary>
/// Moves the refresh token of a successful token response out of the body and into the <c>auth_rt</c> cookie. The
/// refresh token never reaches a response body (spec 0002, criterion 8).
/// </summary>
public sealed class SessionResponseHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ApplyTokenResponseContext>
{
    /// <summary>
    /// Before <see cref="LoginResponseShaper"/>, which rebuilds the body and would drop the refresh token before
    /// this handler could read it.
    /// </summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ApplyTokenResponseContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<SessionResponseHandler>()
            .SetOrder(LoginResponseShaper.Descriptor.Order - 100)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ApplyTokenResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var http = context.Transaction.GetHttpRequest()?.HttpContext
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        if (!string.IsNullOrEmpty(context.Response.Error))
        {
            return ValueTask.CompletedTask;
        }

        // Fail closed: a session without its cookie must not be handed out.
        var refreshToken = context.Response.RefreshToken
            ?? throw new InvalidOperationException("A successful token response has no refresh token.");

        RefreshCookie.Append(http.Response, refreshToken, SessionPolicy.SlidingLifetime);
        context.Response.RefreshToken = null;

        return ValueTask.CompletedTask;
    }
}
