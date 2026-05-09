using Auth.Server.Account;
using Auth.Server.Providers;
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

/// <summary>The endpoints of spec 0012's settings screen, behind the access token and the refresh cookie of the session of this device.</summary>
public static class SettingsRoutes
{
    internal const string RefreshCookieRead =
        "The session of this device: the refresh token of the cookie, read and never redeemed. Missing, or of no live session of the token's "
        + "account: `401 session_required` (a missing or invalid access token is the plain `401` before it).";

    public static IEndpointRouteBuilder MapSettingsApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(AccountConfirmEndpoint.Path, AccountConfirmEndpoint.HandleAsync)
            .RequireAuthorization()
            .WithTags("Account")
            .WithDescription(
                "Confirms who the caller is on this device: the session of the refresh cookie is confirmed for five minutes, so that connecting "
                + "and disconnecting a provider act without asking again (`GET /auth/me` says `confirmed_until`). The body holds exactly one of "
                + "`password` and `provider_ticket` (both or neither: `400 invalid_request`): an account with a password needs it (`403 "
                + "password_required` for a ticket), an account without one confirms with a provider, by a ticket of a round trip with "
                + "`purpose=reauth` and the browser's `auth_pt` (`403 reauth_required` for a password); neither is counted. The password counts in "
                + "the login streak as a failed login does (`403 wrong_password`; while the identifier is locked `429 too_many_attempts`); a "
                + "ticket that is unknown, used, expired (5 minutes), of another purpose, without its `auth_pt` or of an identity that is not a "
                + "login of this account is `403 reauth_failed`, and nothing is used up. An account with no membership is `403 no_membership`.")
            .ReadsJson<ConfirmRequest>()
            .ReadsRefreshCookie(RefreshCookieRead)
            .ReadsCookie(ProviderBindingCookie.Name, "With `provider_ticket`: the binding of the ticket, set by the callback that made it.")
            .Produces(StatusCodes.Status204NoContent)
            .SetsHeader(StatusCodes.Status204NoContent, "Set-Cookie", $"With `provider_ticket`: `{ProviderBindingCookie.Name}` emptied, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status401Unauthorized, TenancyErrors.SessionRequired)
            .ProducesError(
                StatusCodes.Status403Forbidden, TenancyErrors.NoMembership, TenancyErrors.PasswordRequired, TenancyErrors.ReauthRequired,
                TenancyErrors.WrongPassword, TenancyErrors.ReauthFailed)
            .ProducesTooManyAttempts();

        app.MapPost(ProviderPaths.Connect, ProviderConnectEndpoint.HandleAsync)
            .RequireAuthorization()
            .WithTags("Providers")
            .WithDescription(
                "Connects the identity of a ticket of a round trip with `purpose=connect` to the caller's account, while the session of this device "
                + "is confirmed (`GET /auth/me` `confirmed_until`; else `403 confirmation_required`, and the ticket stays usable for its 10 minutes "
                + "as long as no other round trip replaces `auth_pt` in this browser). The checks, in this order: a bad body `400 invalid_request`; "
                + "the session `401 session_required`, no membership `403 no_membership`; a ticket that is unknown, used, expired, not of the purpose "
                + "`connect` or without its `auth_pt` `400 invalid_ticket`; the window; an identity that is already this account's login `204`, the "
                + "ticket used up and nothing else; another account's `409 provider_already_linked`; an account that already has a login of the "
                + "provider `409 provider_in_use` (one per provider: disconnect the old one first); no address `403 no_email`, an unverified one "
                + "`403 email_not_verified`, another address than the account's `403 address_mismatch`. Otherwise the login, `provider.linked` "
                + "(`via` `settings`), the ticket used up and the mail \"provider connected\" are one change; sessions are not touched.")
            .ReadsJson<ProviderTicketRequest>()
            .ReadsRefreshCookie(RefreshCookieRead)
            .ReadsCookie(ProviderBindingCookie.Name, "The binding of the ticket, set by the callback that made it.")
            .Produces(StatusCodes.Status204NoContent)
            .SetsHeader(StatusCodes.Status204NoContent, "Set-Cookie", $"`{ProviderBindingCookie.Name}` emptied, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidTicket)
            .ProducesError(StatusCodes.Status401Unauthorized, TenancyErrors.SessionRequired)
            .ProducesError(
                StatusCodes.Status403Forbidden, TenancyErrors.NoMembership, TenancyErrors.ConfirmationRequired, TenancyErrors.NoEmail,
                TenancyErrors.EmailNotVerified, TenancyErrors.AddressMismatch)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.ProviderAlreadyLinked, TenancyErrors.ProviderInUse)
            .ProducesTooManyAttempts();

        app.MapPost(SettingsRequestHandler.UnlinkPath, LoginUnlinkEndpoint.HandleAsync)
            .RequireAuthorization()
            .WithTags("Account")
            .WithDescription(
                "Disconnects a provider from the caller's account while the session of this device is confirmed (`GET /auth/me` "
                + "`confirmed_until`; else `403 confirmation_required`): every login of that provider is deleted, every session of the account "
                + "ends (its email links and its login streak stay), and this device gets a new session with the start and the window of the old "
                + "one, answered as a login answers. The checks, in this order: a bad body `400 invalid_request`; the session `401 "
                + "session_required`, no membership `403 no_membership`; the window; a name that is not a provider's, or a provider the account has "
                + "no login of, `404 not_found` (a login of a provider no longer offered can be disconnected); the last way in (the password or a "
                + "login of an offered provider) `409 last_login`. A session past its 30-day cap is `401 session_required` and nothing changes. The "
                + "mail \"provider disconnected\" is sent.")
            .ReadsJson<UnlinkRequest>()
            .ReadsRefreshCookie(RefreshCookieRead)
            .Produces<LoginResponse>()
            .SetsRefreshCookie(StatusCodes.Status200OK, AccountEndpoints.RefreshCookieSet)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status401Unauthorized, TenancyErrors.SessionRequired)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.NoMembership, TenancyErrors.ConfirmationRequired)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.LastLogin);

        return app;
    }
}
