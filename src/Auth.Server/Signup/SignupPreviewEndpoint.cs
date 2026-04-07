using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Requests;
using Auth.Server.Tenancy;

namespace Auth.Server.Signup;

/// <summary>
/// <c>POST /auth/signup/preview</c> (spec 0009): what the link of a mail is for. No access token: the link token is the credential. While
/// sign-up is off it answers <c>403 signup_disabled</c> before it reads the body.
/// </summary>
public static class SignupPreviewEndpoint
{
    public const string Path = "/auth/signup/preview";

    private static readonly string[] Fields = ["token"];

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
        if (fields is null)
        {
            return AccountResults.InvalidRequest();
        }

        var preview = await signup.PreviewAsync(fields[0], http.RequestAborted);
        return preview is null
            ? AccountResults.InvalidToken()
            : ApiResults.Ok(new SignupPreviewResponse(preview.Email, preview.CompanyName, preview.HasAccount));
    }
}
