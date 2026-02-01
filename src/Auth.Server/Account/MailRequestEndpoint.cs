using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Account;

/// <summary>
/// <c>POST /auth/password/forgot</c> and <c>POST /auth/email/verify/request</c>: take a request for a mail. The
/// two differ only in the kind of mail. The handler validates, applies the limit of the address, queues the request
/// and answers; it never looks an account up, so its work and its answer are the same for every address
/// (spec 0004, Decision 12). Whether a mail follows is decided later, by the dispatcher.
/// </summary>
public static class MailRequestEndpoint
{
    public const string ForgotPasswordPath = "/auth/password/forgot";
    public const string VerifyEmailRequestPath = "/auth/email/verify/request";

    private static readonly string[] Fields = ["email"];

    public static async Task<IResult> HandleAsync(
        MailKind kind, HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(signal);

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        if (fields is null
            || fields[0].Length > EmailInput.MaxLength
            || !EmailInput.TryNormalize(fields[0], normalizer, out var normalized))
        {
            return AccountResults.InvalidRequest();
        }

        var decision = await requests.SubmitAsync(kind, normalized, http.RequestAborted);
        if (!decision.Allowed)
        {
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        signal.Notify();
        return AccountResults.Accepted();
    }
}
