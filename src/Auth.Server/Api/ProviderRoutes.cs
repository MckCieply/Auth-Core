using Auth.Server.Account;
using Auth.Server.Login;
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
                + "expired, of the purpose `reauth` or without its `auth_pt` is `400 invalid_ticket`. A ticket of an invitation (`invite`) accepts it "
                + "and signs in, with the account made without a password: the invitation unknown, used, expired, cancelled or replaced is "
                + "`400 invalid_token`; the ticket's address missing, unverified or not the invited one `403 address_mismatch`; a company member's address "
                + "`409 already_member`, a private account's `409 private_account_exists`, an identity linked already `409 provider_already_linked`; the "
                + "invitation stays usable after each of them.")
            .ReadsJson<ProviderTicketRequest>()
            .ReadsCookie(ProviderBindingCookie.Name, "The binding of the ticket, set by the callback that made it. Without it, or with another browser's, the ticket is unknown.")
            .Produces<ProviderExchangeResponse>()
            .SetsRefreshCookie(
                StatusCodes.Status200OK,
                AccountEndpoints.RefreshCookieSet + $" On a sign-in only; `{ProviderBindingCookie.Name}` is emptied then, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidTicket, TenancyErrors.InvalidToken)
            .ProducesError(
                StatusCodes.Status403Forbidden, TenancyErrors.NoMembership, TenancyErrors.NoEmail, TenancyErrors.NoAccount, TenancyErrors.AddressMismatch)
            .ProducesError(
                StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered, TenancyErrors.AlreadyMember, TenancyErrors.PrivateAccountExists,
                TenancyErrors.ProviderAlreadyLinked);

        app.MapPost(ProviderPaths.Link, ProviderLinkEndpoint.HandleAsync)
            .WithTags("Providers")
            .WithDescription(
                "Links the identity of a sign-in ticket to the account with the ticket's address, with that account's password, once; from then on "
                + "the provider alone signs in. The password counts in the login streak of the address as a login does: while the identifier is "
                + "locked the answer is `429 too_many_attempts`. No account with the address (spelled the same up to ASCII case), an account without a "
                + "password, and a wrong password are the same `401 invalid_credentials`, and the ticket stays usable. With the right password an "
                + "unconfirmed address is confirmed when the provider verified it (else `403 email_not_verified`), and an account with no membership is "
                + "`403 no_membership`. Then the login, `provider.linked`, the use of the ticket and the session are one change: `200` as a login "
                + "answers, `auth_pt` cleared. An identity linked meanwhile signs in without the password being judged. A ticket without an address is "
                + "`403 no_email`; one that is unknown, used, expired, not of the purpose `sign_in` or without its `auth_pt` is `400 invalid_ticket`.")
            .ReadsJson<ProviderLinkRequest>()
            .ReadsCookie(ProviderBindingCookie.Name, "The binding of the ticket, set by the callback that made it.")
            .Produces<LoginResponse>()
            .SetsRefreshCookie(
                StatusCodes.Status200OK, AccountEndpoints.RefreshCookieSet + $" `{ProviderBindingCookie.Name}` is emptied, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidTicket)
            .ProducesError(StatusCodes.Status401Unauthorized, LoginEndpoint.InvalidCredentialsError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.NoEmail, TenancyErrors.EmailNotVerified, TenancyErrors.NoMembership)
            .ProducesTooManyAttempts();

        app.MapPost(ProviderPaths.Finish, ProviderFinishEndpoint.HandleAsync)
            .WithTags("Providers")
            .WithDescription(
                "A new person finishes creating their account with the verified address of a sign-in ticket, with no password and no mail, and is "
                + "signed in at once: `200` as a login answers, `auth_pt` cleared. In one transaction the account (its email confirmed), the identity "
                + "as its login, the space (`kind` `organization` with `company_name`: the company with a copy of the default roles and the first "
                + "admin's role; `private`: the personal space; absent: an organization), the deletion of the address's pending sign-up and "
                + "`signup.completed` are made. The checks: both sign-up switches off `403 signup_disabled` before the body is read; a bad body "
                + "`400 invalid_request`; a ticket that is unknown, used, expired, not of the purpose `sign_in` or without its `auth_pt` "
                + "`400 invalid_ticket`; no address or an unverified one `403 email_not_verified`; the switch of the kind off `403 signup_disabled`; an "
                + "organization without, or a private finish with, `company_name` `400 invalid_request` (the ticket stays usable); any account under "
                + "the address, or the identity linked meanwhile, `409 already_registered`.")
            .ReadsJson<ProviderFinishRequest>()
            .ReadsCookie(ProviderBindingCookie.Name, "The binding of the ticket, set by the callback that made it.")
            .Produces<LoginResponse>()
            .SetsRefreshCookie(
                StatusCodes.Status200OK, AccountEndpoints.RefreshCookieSet + $" `{ProviderBindingCookie.Name}` is emptied, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidTicket)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled, TenancyErrors.EmailNotVerified)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered);

        return app;
    }
}
