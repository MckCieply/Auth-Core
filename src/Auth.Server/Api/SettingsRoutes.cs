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

        return app;
    }
}
