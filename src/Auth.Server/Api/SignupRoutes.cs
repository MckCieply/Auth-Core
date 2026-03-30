using Auth.Server.Signup;

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

        return app;
    }
}
