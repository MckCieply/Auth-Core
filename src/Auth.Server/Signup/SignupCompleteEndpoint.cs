using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Requests;
using Auth.Server.Tenancy;

namespace Auth.Server.Signup;

/// <summary>
/// <c>POST /auth/signup/complete</c> (spec 0009): finishes a sign-up. No access token: the link token is the credential. While sign-up is
/// off it answers <c>403 signup_disabled</c> before it reads the body — also for a request that was made while it was on. The person is not
/// signed in: the product's screen sends them to its sign-in screen.
/// </summary>
public static class SignupCompleteEndpoint
{
    public const string Path = "/auth/signup/complete";

    private static readonly string[] Fields = ["token", "password", "company_name"];

    public static async Task<IResult> HandleAsync(HttpContext http, SignupGate gate, SignupService signup)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(signup);

        if (!gate.IsOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        if (fields is null || fields[1].Contains('\0') || !NameInput.IsValid(fields[2]))
        {
            return AccountResults.InvalidRequest();
        }

        var done = await signup.CompleteAsync(fields[0], fields[1], fields[2], http.RequestAborted);
        if (done.Succeeded)
        {
            return ApiResults.NoContent();
        }

        return done.Error == TenancyErrors.WeakPassword
            ? AccountResults.WeakPassword(done.Value!)
            : ApiResults.Error(done.Error!);
    }
}
