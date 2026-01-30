using Microsoft.Net.Http.Headers;

namespace Auth.Server.Account;

/// <summary>
/// The responses of the account endpoints (spec 0004 → Contract). Every one of them is marked as never to be
/// stored, like the token responses, and none touches the refresh cookie.
/// </summary>
public static class AccountResults
{
    public const string InvalidRequestError = "invalid_request";
    public const string InvalidTokenError = "invalid_token";
    public const string WeakPasswordError = "weak_password";
    public const string EmailNotVerifiedError = "email_not_verified";

    /// <summary>202: the request was taken. Says nothing about whether a mail will follow.</summary>
    public static IResult Accepted() => new NoStoreResult(StatusCodes.Status202Accepted, null);

    /// <summary>204: the token was used and its effects applied.</summary>
    public static IResult Done() => new NoStoreResult(StatusCodes.Status204NoContent, null);

    public static IResult InvalidRequest() =>
        new NoStoreResult(StatusCodes.Status400BadRequest, new { error = InvalidRequestError });

    /// <summary>One answer for a token that is unknown, used, expired or replaced.</summary>
    public static IResult InvalidToken() =>
        new NoStoreResult(StatusCodes.Status400BadRequest, new { error = InvalidTokenError });

    public static IResult WeakPassword(IReadOnlyList<string> rules) =>
        new NoStoreResult(StatusCodes.Status400BadRequest, new { error = WeakPasswordError, rules });

    public static IResult EmailNotVerified() =>
        new NoStoreResult(StatusCodes.Status403Forbidden, new { error = EmailNotVerifiedError });

    private sealed class NoStoreResult(int statusCode, object? body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            httpContext.Response.Headers[HeaderNames.CacheControl] = "no-store";
            httpContext.Response.Headers[HeaderNames.Pragma] = "no-cache";
            if (body is null)
            {
                httpContext.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            }

            return Results.Json(body, statusCode: statusCode).ExecuteAsync(httpContext);
        }
    }
}
