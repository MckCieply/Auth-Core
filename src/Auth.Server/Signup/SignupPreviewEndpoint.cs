using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Requests;
using Auth.Server.Tenancy;

namespace Auth.Server.Signup;

/// <summary>
/// <c>POST /auth/signup/preview</c> (specs 0009 and 0010): what the link of a mail is for. No access token: the link token is the credential.
/// While both switches are off it answers <c>403 signup_disabled</c> before it reads the body; while the switch of the link's kind is off,
/// after the token.
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

        if (!gate.AnyOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        if (fields is null)
        {
            return AccountResults.InvalidRequest();
        }

        var preview = await signup.PreviewAsync(fields[0], http.RequestAborted);
        if (preview is null)
        {
            return AccountResults.InvalidToken();
        }

        // The kind is known only from the token: its switch is judged now (spec 0010 → preview, step 3).
        return gate.IsOnFor(preview.Kind)
            ? ApiResults.Ok(new SignupPreviewResponse(SignupKinds.NameOf(preview.Kind), preview.Email, preview.CompanyName))
            : ApiResults.Error(TenancyErrors.SignupDisabled);
    }
}
