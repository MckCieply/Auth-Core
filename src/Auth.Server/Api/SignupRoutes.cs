using Auth.Server.Account;
using Auth.Server.Signup;
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

public static class SignupRoutes
{
    /// <summary>
    /// The endpoints of spec 0009: the options a product's screens read, and the three steps of a self-service sign-up. Mapped here, with
    /// what each takes and answers, so that the OpenAPI description says it all in one place.
    /// </summary>
    public static IEndpointRouteBuilder MapSignupApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(SignupOptionsEndpoint.Path, SignupOptionsEndpoint.Handle)
            .WithTags("Sign-up")
            .WithDescription(
                "What a product's screens may offer. `signup` is `self-service` while organization sign-up is on and `invite-only` otherwise; "
                + "`private_signup` is `self-service` while private sign-up is on and `off` otherwise. A client ignores members it does not know.")
            .Produces<OptionsResponse>();

        app.MapPost(SignupRequestEndpoint.Path, SignupRequestEndpoint.HandleAsync)
            .WithTags("Sign-up")
            .WithDescription(
                "Asks to start a company (`kind` `organization`, the default, with `company_name`) or to open a private account (`kind` `private`, "
                + "no `company_name`). The answer is the same `202` for every address; a mail follows: a notice without a link when the address has an "
                + "account, of either kind, otherwise a link to finish. `403 signup_disabled` while the switch of the kind is off (with both off, before "
                + "the body is read). One request a minute and five an hour per address, for both kinds together.")
            .ReadsJson<SignupRequest>()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled)
            .ProducesTooManyAttempts();

        app.MapPost(SignupPreviewEndpoint.Path, SignupPreviewEndpoint.HandleAsync)
            .WithTags("Sign-up")
            .WithDescription(
                "Shows what the link of the mail is for: `kind` (`organization` or `private`), the address as it was typed and, for an organization, "
                + "the company name of the request. It changes nothing and the link stays usable. A token that is unknown, used, expired or replaced "
                + "by a newer mail is `invalid_token`; a link whose kind of sign-up is off is `403 signup_disabled`.")
            .ReadsJson<SignupPreviewRequest>()
            .Produces<SignupPreviewResponse>()
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError, AccountResults.InvalidTokenError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled);

        app.MapPost(SignupCompleteEndpoint.Path, SignupCompleteEndpoint.HandleAsync)
            .WithTags("Sign-up")
            .WithDescription(
                "Finishes a sign-up and signs the person in: in one transaction the account (its email confirmed) and its space are made (an organization: "
                + "the company named as given here, with a copy of the default roles, and the membership with the first admin's role; a private person: "
                + "the personal space), the pending sign-up is used up, and the session is issued; if the session cannot be written nothing of this stays. "
                + "The answer is that of a successful login: the access token (carrying the new space, `org_kind` and the role) and the refresh cookie. "
                + "The checks are made in this order: the token, the switch of its kind (`403 signup_disabled`), the company name (required for an "
                + "organization, refused for a private sign-up: `400 invalid_request`, the token stays usable), any account under the address "
                + "(`409 already_registered`, whatever the password), the password (missing: `400 invalid_request`; weak: `400 weak_password`; the "
                + "token stays usable).")
            .ReadsJson<SignupCompleteRequest>()
            .Produces<LoginResponse>()
            .SetsRefreshCookie(StatusCodes.Status200OK, AccountEndpoints.RefreshCookieSet + " The person is signed in, as after a login.")
            .ProducesPasswordError(AccountResults.InvalidRequestError, AccountResults.InvalidTokenError, AccountResults.WeakPasswordError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered);

        return app;
    }
}
