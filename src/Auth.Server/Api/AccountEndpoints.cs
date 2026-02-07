using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Email;
using Auth.Server.Login;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Api;

public static class AccountEndpoints
{
    public const string HealthPath = "/auth/health";

    private static readonly string RefreshCookieSet =
        $"`{RefreshCookie.Name}`: the refresh token of the session, `HttpOnly`, `Secure`, `SameSite=Strict`, path `{RefreshCookie.Path}`, "
        + "living as long as the session may still be renewed.";

    /// <summary>
    /// The endpoints of specs 0001–0004: login, refresh, logout, and the two flows by mail. Mapped here, with what each takes
    /// and answers, so that the OpenAPI description (spec 0005) says it all in one place. The metadata only describes: the
    /// handlers read and answer the bodies themselves, and a request that is not what they take is their <c>400</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapAccountApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The health check has no OpenAPI metadata of its own: the description adds it (see OpenApiSetup).
        app.MapHealthChecks(HealthPath);

        app.MapPost(JsonLoginRequestHandler.LoginPath, LoginEndpoint.HandleAsync)
            .WithTags("Sessions")
            .ReadsJson<LoginRequest>()
            .Produces<LoginResponse>()
            .SetsRefreshCookie(StatusCodes.Status200OK, RefreshCookieSet + " It never appears in a body.")
            .ProducesError(StatusCodes.Status400BadRequest, "invalid_request")
            .ProducesError(StatusCodes.Status401Unauthorized, LoginEndpoint.InvalidCredentialsError)
            .ProducesError(StatusCodes.Status403Forbidden, AccountResults.EmailNotVerifiedError, AccountResults.NoMembershipError)
            .ProducesTooManyAttempts();
        app.MapPost(RefreshRequestHandler.RefreshPath, RefreshEndpoint.HandleAsync)
            .WithTags("Sessions")
            .ReadsRefreshCookie(
                "The refresh token set by the login or the last refresh. Missing, unknown, expired, revoked or used up: "
                + "`401 invalid_grant`.")
            .Produces<RefreshResponse>()
            .SetsRefreshCookie(StatusCodes.Status200OK, RefreshCookieSet + " The token that was sent is used up.")
            .ProducesError(StatusCodes.Status401Unauthorized, "invalid_grant");
        app.MapPost(LogoutEndpoint.LogoutPath, LogoutEndpoint.HandleAsync)
            .WithTags("Sessions")
            .ReadsRefreshCookie("The refresh token whose session ends. Missing or unknown: the answer is the same `204`.")
            .Produces(StatusCodes.Status204NoContent)
            .SetsRefreshCookie(
                StatusCodes.Status204NoContent, $"`{RefreshCookie.Name}` emptied, with `Max-Age=0`: the browser drops the cookie.");

        app.MapPost(
                MailRequestEndpoint.ForgotPasswordPath,
                (HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal) =>
                    MailRequestEndpoint.HandleAsync(MailKind.PasswordReset, http, normalizer, requests, signal))
            .WithTags("Account")
            .ReadsJson<EmailRequest>()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError)
            .ProducesTooManyAttempts();
        app.MapPost(
                MailRequestEndpoint.VerifyEmailRequestPath,
                (HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal) =>
                    MailRequestEndpoint.HandleAsync(MailKind.EmailVerification, http, normalizer, requests, signal))
            .WithTags("Account")
            .ReadsJson<EmailRequest>()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError)
            .ProducesTooManyAttempts();
        app.MapPost(ResetPasswordEndpoint.Path, ResetPasswordEndpoint.HandleAsync)
            .WithTags("Account")
            .ReadsJson<ResetPasswordRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesPasswordError(AccountResults.InvalidRequestError, AccountResults.InvalidTokenError, AccountResults.WeakPasswordError);
        app.MapPost(VerifyEmailEndpoint.Path, VerifyEmailEndpoint.HandleAsync)
            .WithTags("Account")
            .ReadsJson<TokenRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError, AccountResults.InvalidTokenError);

        return app;
    }
}
