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
                "What a product's screens may offer. `signup` is `self-service` while sign-up is on and `invite-only` otherwise. "
                + "A client ignores members it does not know.")
            .Produces<OptionsResponse>();

        app.MapPost(SignupRequestEndpoint.Path, SignupRequestEndpoint.HandleAsync)
            .WithTags("Sign-up")
            .WithDescription(
                "Asks to start a company. The answer is the same `202` for every address; a mail follows: a link to finish when the address "
                + "has no account, a notice without a link when it has one. Off unless the manifest says `signup: self-service` and the "
                + "completion address is configured (`403 signup_disabled`). One request a minute and five an hour per address.")
            .ReadsJson<SignupRequest>()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled)
            .ProducesTooManyAttempts();

        return app;
    }
}
