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
                "Asks to start a company. The answer is the same `202` for every address; a mail follows: a notice without a link when the address "
                + "has an account that belongs to a company or only resembles the typed address, otherwise a link to finish (also when the account "
                + "belongs to no company). Off unless the "
                + "manifest says `signup: self-service` and the completion address is configured (`403 signup_disabled`). One request a minute and "
                + "five an hour per address.")
            .ReadsJson<SignupRequest>()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled)
            .ProducesTooManyAttempts();

        app.MapPost(SignupPreviewEndpoint.Path, SignupPreviewEndpoint.HandleAsync)
            .WithTags("Sign-up")
            .WithDescription(
                "Shows what the link of the mail is for: the address as it was typed, the company name of the request and `has_account`, which is true "
                + "only when an account without a company has the address (then the completion needs no password). It changes nothing "
                + "and the link stays usable. A token that is unknown, used, expired or replaced by a newer mail is `invalid_token`.")
            .ReadsJson<SignupPreviewRequest>()
            .Produces<SignupPreviewResponse>()
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError, AccountResults.InvalidTokenError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled);

        app.MapPost(SignupCompleteEndpoint.Path, SignupCompleteEndpoint.HandleAsync)
            .WithTags("Sign-up")
            .WithDescription(
                "Finishes a sign-up and signs the person in: in one transaction the account (its email confirmed), the company (named as given here, "
                + "with a copy of the default roles) and the membership with the first admin's role are made, the pending sign-up is used up, and the "
                + "session is issued; if the session cannot be written nothing of this stays. The answer is that of a successful login: the access token "
                + "(carrying the new company and the first admin's role) and the refresh cookie. The checks are made in this order: the token, the "
                + "account (`409 already_registered`, whatever the password, when an account that belongs to a company, or one that only resembles the "
                + "typed address, has the address by now), the password (only where no account exists: a weak one is `400 weak_password` and leaves "
                + "the token usable; an account that has no company keeps the password it has, so for it the member may be left out and is not used if sent).")
            .ReadsJson<SignupCompleteRequest>()
            .Produces<LoginResponse>()
            .SetsRefreshCookie(StatusCodes.Status200OK, AccountEndpoints.RefreshCookieSet + " The person is signed in, as after a login.")
            .ProducesPasswordError(AccountResults.InvalidRequestError, AccountResults.InvalidTokenError, AccountResults.WeakPasswordError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered);

        return app;
    }
}
