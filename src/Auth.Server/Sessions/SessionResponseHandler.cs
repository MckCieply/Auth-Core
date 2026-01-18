using System.Text.Json;
using Auth.Server.Login;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Sessions;

/// <summary>
/// Moves the refresh token of a successful token response out of the body and into the <c>auth_rt</c> cookie. The
/// refresh token never reaches a response body (spec 0002, criterion 8). On <c>/auth/refresh</c> it also writes the
/// response itself: <c>{"access_token"}</c> on success, one uniform <c>401 {"error":"invalid_grant"}</c> on failure.
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
    public async ValueTask HandleAsync(OpenIddictServerEvents.ApplyTokenResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var http = context.Transaction.GetHttpRequest()?.HttpContext
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        var isRefresh = RefreshRequestHandler.IsRefreshPath(http.Request.Path);

        if (!string.IsNullOrEmpty(context.Response.Error))
        {
            // invalid_request is the wrong-method case: a malformed request, answered like login's 400. Every other
            // error on this path is a refresh failure and gets the one uniform answer (spec 0002, criterion 7).
            if (isRefresh && !string.Equals(context.Response.Error, Errors.InvalidRequest, StringComparison.Ordinal))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await WriteCompactAsync(http.Response, new OpenIddictResponse { Error = Errors.InvalidGrant }, context);
            }

            return;
        }

        // Fail closed: a session without its cookie must not be handed out.
        var refreshToken = context.Response.RefreshToken
            ?? throw new InvalidOperationException("A successful token response has no refresh token.");

        var lifetime = http.Items[RefreshCookie.LifetimeItemKey] as TimeSpan?
            ?? throw new InvalidOperationException("The endpoint did not record the refresh token lifetime.");

        // Checked before the cookie is written, so a response about to fail never carries a Set-Cookie.
        var accessToken = context.Response.AccessToken;
        if (isRefresh && accessToken is null)
        {
            throw new InvalidOperationException("A successful token response has no access token.");
        }

        RefreshCookie.Append(http.Response, refreshToken, lifetime);
        context.Response.RefreshToken = null;

        if (isRefresh)
        {
            // Replace, rather than remove one by one: whatever else OpenIddict adds must not leak into the contract.
            await WriteCompactAsync(http.Response, new OpenIddictResponse { AccessToken = accessToken }, context);
        }
    }

    /// <summary>
    /// Writes the refresh response itself, because OpenIddict's JSON writer indents its output and the contract is
    /// the compact <c>{"access_token":"…"}</c> / <c>{"error":"invalid_grant"}</c>. It sets the same headers as that
    /// writer and, like it, marks the request handled so nothing writes to the response a second time.
    /// </summary>
    private static async ValueTask WriteCompactAsync(
        HttpResponse response, OpenIddictResponse body, OpenIddictServerEvents.ApplyTokenResponseContext context)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.ContentType = "application/json;charset=UTF-8";

        await JsonSerializer.SerializeAsync(response.Body, body, cancellationToken: context.CancellationToken);
        context.HandleRequest();
    }
}
