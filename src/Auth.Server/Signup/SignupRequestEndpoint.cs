using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Signup;

/// <summary>
/// <c>POST /auth/signup</c> (spec 0009): takes a request to start a company. While sign-up is off it answers <c>403 signup_disabled</c>
/// before it reads the body. Otherwise it validates, applies the limit of the address, stores the pending sign-up, queues the request and
/// answers; it never looks an account up, so its work and its answer are the same for every address. Which mail follows is decided when
/// the mail is sent.
/// </summary>
public static class SignupRequestEndpoint
{
    public const string Path = "/auth/signup";

    private static readonly string[] Fields = ["email", "company_name"];

    public static async Task<IResult> HandleAsync(
        HttpContext http, SignupGate gate, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(signal);

        if (!gate.IsOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        // The address rule of invitations (spec 0005), so that sign-up cannot make the mail relay deliver to an internal host, and the
        // name rule of companies.
        if (fields is null
            || fields[0].Length > EmailInput.MaxLength
            || !EmailInput.IsMailbox(fields[0])
            || !EmailInput.IsInvitable(fields[0])
            || !EmailInput.TryNormalize(fields[0], normalizer, out var normalized)
            || !NameInput.IsValid(fields[1]))
        {
            return AccountResults.InvalidRequest();
        }

        var decision = await requests.SubmitAsync(MailKind.Signup, normalized, http.RequestAborted, typedEmail: fields[0], companyName: fields[1]);
        if (!decision.Allowed)
        {
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        signal.Notify();
        return AccountResults.Accepted();
    }
}
