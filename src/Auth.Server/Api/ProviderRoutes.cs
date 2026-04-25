using Auth.Server.Providers;
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

public static class ProviderRoutes
{
    /// <summary>
    /// The endpoints of spec 0011: the round trip with Google or Microsoft and what a product's screens do with its ticket. Mapped here, with what
    /// each takes and answers, so that the OpenAPI description says it all in one place.
    /// </summary>
    public static IEndpointRouteBuilder MapProviderApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(ProviderPaths.Start, ProviderStartEndpoint.Handle)
            .WithTags("Providers")
            .WithDescription(
                "Starts a sign-in with Google or Microsoft: a product's button is a link here (a navigation, not a call). The answer is `302` to the "
                + "provider's authorization endpoint: the authorization code flow with PKCE (`S256`), a `state`, a `nonce`, the scopes `openid email "
                + "profile` and the callback address built from the configured return address, never from the request. No query: a sign-in, a link or a "
                + "sign-up (the exchange decides); `invite=<link token>`: the acceptance of that invitation; `purpose=reauth`: the confirmation of a "
                + "self-deletion, with a fresh sign-in asked for. It reads no account and no invitation. A name that is not `google` or `microsoft`, or a "
                + "provider that is not offered, is `404 not_found`; a malformed `invite`, another `purpose`, both, or one of them given twice is "
                + "`400 invalid_request`. Other parameters are ignored.")
            .ReadsQuery(ProviderStartEndpoint.InviteParameter, "The link token of an invitation (43 base64url characters): the round trip accepts it.")
            .ReadsQuery(ProviderStartEndpoint.PurposeParameter, "`reauth`: the round trip confirms the deletion of the caller's private account.")
            .Produces(StatusCodes.Status302Found)
            .SetsHeader(StatusCodes.Status302Found, "Location", "The provider's authorization endpoint with the request of the round trip.")
            .SetsHeader(
                StatusCodes.Status302Found, "Set-Cookie",
                "`OpenIddict.Client.State.<...>`: binds the state of the round trip to this browser; `HttpOnly`, `Secure`, `SameSite=Lax`, path "
                + "`/auth/providers`, for the 10 minutes of the state.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound);

        return app;
    }
}
