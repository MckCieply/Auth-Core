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
/// <c>POST /auth/signup</c> (specs 0009 and 0010): a request to start a company or to open a private account. While both switches are off it
/// answers <c>403 signup_disabled</c> before it reads the body. The body is read in two phases: the kind first (absent means an organization),
/// and its switch is judged before the other members, so that a screen that offers only one kind never meets a <c>400</c> of the other. Then it
/// validates, applies the limit of the address (one counter for both kinds), stores the pending sign-up with its kind, queues the request and
/// answers; it never looks an account up, so its work and its answer are the same for every address. Which mail follows is decided when the
/// mail is sent.
/// </summary>
public static class SignupRequestEndpoint
{
    public const string Path = "/auth/signup";

    private const string EmailField = "email";
    private const string CompanyNameField = "company_name";

    public static async Task<IResult> HandleAsync(
        HttpContext http, SignupGate gate, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(signal);

        if (!gate.AnyOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || !SignupKinds.TryRead(fields, out var kind))
        {
            return AccountResults.InvalidRequest();
        }

        if (!gate.IsOnFor(kind))
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        // The address rule of invitations (spec 0005), so that sign-up cannot make the mail relay deliver to an internal host.
        if (!fields.TryString(EmailField, out var email)
            || email.Length > EmailInput.MaxLength
            || !EmailInput.IsMailbox(email)
            || !EmailInput.IsInvitable(email)
            || !EmailInput.TryNormalize(email, normalizer, out var normalized))
        {
            return AccountResults.InvalidRequest();
        }

        string? companyName = null;
        if (kind == SignupKind.Organization)
        {
            if (!fields.TryString(CompanyNameField, out var name) || !NameInput.IsValid(name))
            {
                return AccountResults.InvalidRequest();
            }

            companyName = name;
        }
        else if (fields.Has(CompanyNameField))
        {
            return AccountResults.InvalidRequest();   // a private person names no company, whatever the value (spec 0010, Decision 5)
        }

        var decision = await requests.SubmitAsync(
            MailKind.Signup, normalized, http.RequestAborted, typedEmail: email, companyName: companyName, signupKind: kind);
        if (!decision.Allowed)
        {
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        signal.Notify();
        return AccountResults.Accepted();
    }
}
