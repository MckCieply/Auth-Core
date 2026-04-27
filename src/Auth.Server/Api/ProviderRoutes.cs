using Auth.Server.Account;
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

        // GET only: the response mode is `query` (probe Q2); a form post would be a cross-site form, which the JSON-only rule refuses.
        app.MapGet(ProviderPaths.Callback, ProviderCallbackEndpoint.HandleAsync)
            .WithTags("Providers")
            .WithDescription(
                "Where the provider sends the browser back. OpenIddict checks the state (its signature, its age, its single use, the request-forgery "
                + "cookie of the browser that started), redeems the code with the PKCE verifier and validates the identity token. The answer is always "
                + "`302` to the product's return screen (`Auth:App:FrontendUrls:ProviderReturn`), never a page: on success "
                + "`#ticket=<43 characters>&purpose=<sign_in|invite|reauth>` and the cookie `auth_pt`; on failure `#error=<code>&purpose=<purpose>` "
                + "(`purpose` left out when the state could not be read) with the codes `cancelled`, `invalid_state`, `provider_unavailable` and "
                + "`provider_error`, and nothing stored. The ticket lives 10 minutes (5 for `reauth`) and works only with the `auth_pt` of this browser. "
                + "A name that is not a provider, or an instance without the return address, is `404 not_found`.")
            .Produces(StatusCodes.Status302Found)
            .SetsHeader(
                StatusCodes.Status302Found, "Location",
                "The return screen with the fragment: `ticket` and `purpose` on success, `error` and `purpose` on failure.")
            .SetsHeader(
                StatusCodes.Status302Found, "Set-Cookie",
                $"`{ProviderBindingCookie.Name}` on success: binds the ticket to this browser; `HttpOnly`, `Secure`, `SameSite=Strict`, path "
                + $"`{ProviderBindingCookie.Path}`, `Max-Age=600`. OpenIddict's request-forgery cookie is deleted.")
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound);

        app.MapPost(ProviderPaths.Exchange, ProviderExchangeEndpoint.HandleAsync)
            .WithTags("Providers")
            .WithDescription(
                "What a ticket of the callback leads to; the product's return screen calls it with the ticket of the fragment and the browser's "
                + "`auth_pt` cookie. An identity that is a login of an account signs in: `200` as a login answers (the access token, the refresh "
                + "cookie), the ticket used up and `auth_pt` cleared; an account with no membership is `403 no_membership`. Otherwise the ticket stays "
                + "usable and the answer says what comes next: no address `403 no_email`; an address the provider did not verify "
                + "`{\"next\":\"link\",\"email\",\"email_verified\":false}`, whether or not an account has it; an account with the address "
                + "`{\"next\":\"link\",\"email\",\"email_verified\":true}`; an account under a look-alike spelling `409 already_registered`; no account "
                + "`{\"next\":\"finish_signup\",\"email\",\"kinds\"}` while sign-up is on, else `403 no_account`. A ticket that is unknown, used, "
                + "expired, of the purpose `reauth` or without its `auth_pt` is `400 invalid_ticket`.")
            .ReadsJson<ProviderTicketRequest>()
            .ReadsCookie(ProviderBindingCookie.Name, "The binding of the ticket, set by the callback that made it. Without it, or with another browser's, the ticket is unknown.")
            .Produces<ProviderExchangeResponse>()
            .SetsRefreshCookie(
                StatusCodes.Status200OK,
                AccountEndpoints.RefreshCookieSet + $" On a sign-in only; `{ProviderBindingCookie.Name}` is emptied then, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidTicket)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.NoMembership, TenancyErrors.NoEmail, TenancyErrors.NoAccount)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered);

        return app;
    }
}
