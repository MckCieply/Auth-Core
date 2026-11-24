using Auth.Server.Login;
using Auth.Server.Requests;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Sessions;

/// <summary>
/// Turns <c>POST /auth/switch</c> into an OpenIddict refresh-token request (spec 0015). The body is a JSON object <c>{"org_id":"…"}</c>, read by
/// <see cref="JsonObjectBody.ReadObjectAsync"/>, and the <c>auth_rt</c> cookie is the token, as on <c>/auth/refresh</c>. Every rejection happens here,
/// before OpenIddict reads the token, so nothing is redeemed: a body that is not acceptable is <c>invalid_request</c> (checked first), then a
/// missing cookie is <c>invalid_grant</c>. The <c>org_id</c> travels on as a parameter of the request, as sent: the endpoint parses it again.
/// </summary>
public sealed class SwitchRequestHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>
{
    /// <summary>The request parameter that carries the id of the space to switch to.</summary>
    public const string OrgIdParameter = "org_id";

    private const string InvalidBodyDescription = "The request body must be 'application/json': a JSON object of at most 8 KiB with a string property 'org_id' that is a UUID.";

    /// <summary>After <see cref="RefreshRequestHandler"/>, before <see cref="UnhandledTokenRequestGuard"/>.</summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ExtractTokenRequestContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<SwitchRequestHandler>()
            .SetOrder(RefreshRequestHandler.Descriptor.Order + 25)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public async ValueTask HandleAsync(OpenIddictServerEvents.ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Transaction.GetHttpRequest()
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        if (!SessionPaths.IsSwitchPath(request.Path))
        {
            return;
        }

        if (!HttpMethods.IsPost(request.Method))
        {
            Reject(context, "The switch endpoint only accepts POST requests.");
            return;
        }

        // The one place that judges a body (media type, size, JSON object, no member name that cannot be read), then the member itself.
        // IdInput accepts only the 36-character "D" form: braces, no hyphens and white space are refused here, not by Guid.TryParse.
        if (await JsonObjectBody.ReadObjectAsync(request, context.CancellationToken) is not { } fields
            || !fields.TryString(OrgIdParameter, out var orgId)
            || !IdInput.TryParse(orgId, out _))
        {
            Reject(context, InvalidBodyDescription);
            return;
        }

        // The body is judged first: a malformed one is a 400 whatever the cookie says. Only then does a missing cookie end the request.
        if (!RefreshCookie.TryRead(request, out var token))
        {
            context.Reject(error: Errors.InvalidGrant);
            return;
        }

        var refresh = new OpenIddictRequest
        {
            GrantType = GrantTypes.RefreshToken,
            RefreshToken = token,
        };
        refresh.SetParameter(OrgIdParameter, orgId);
        context.Request = refresh;
    }

    private static void Reject(OpenIddictServerEvents.ExtractTokenRequestContext context, string description) =>
        context.Reject(error: Errors.InvalidRequest, description: description);
}
