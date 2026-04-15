using Auth.Server.Account;
using Auth.Server.Requests;
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

/// <summary>
/// <c>POST /auth/invites/preview</c> and <c>POST /auth/invites/accept</c> (spec 0005 → Invitations — public). They need
/// no access token: the link token is the credential, and whoever holds it can see and accept the invitation.
/// </summary>
public static class InviteEndpoints
{
    public const string PreviewPath = "/auth/invites/preview";
    public const string AcceptPath = "/auth/invites/accept";

    private static readonly string[] PreviewFields = ["token"];

    public static async Task<IResult> PreviewAsync(HttpContext http, InviteAcceptance acceptance)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(acceptance);

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, PreviewFields, http.RequestAborted);
        if (fields is null)
        {
            return ApiResults.InvalidRequest();
        }

        var preview = await acceptance.PreviewAsync(fields[0], http.RequestAborted);
        return preview.Succeeded
            ? ApiResults.Ok(new InvitePreviewResponse(preview.Value!.OrgName, preview.Value.Email, preview.Value.Role, preview.Value.HasPrivateAccount))
            : ApiResults.Error(preview.Error!);
    }

    public static async Task<IResult> AcceptAsync(HttpContext http, InviteAcceptance acceptance)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(acceptance);

        // Spec 0010: the password is needed only where an account is made, so it is optional here and judged after the token; when it is there it
        // is a non-blank string with no NUL. leave_private_account is a JSON boolean when it is there.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields
            || !fields.TryString("token", out var token)
            || !fields.TryOptionalString("password", out var password)
            || password?.Contains('\0') == true)
        {
            return ApiResults.InvalidRequest();
        }

        var leave = fields.Boolean("leave_private_account");
        if (leave == JsonBoolean.Invalid)
        {
            return ApiResults.InvalidRequest();
        }

        var accepted = await acceptance.AcceptAsync(token, password, leave == JsonBoolean.True, http.RequestAborted);
        if (accepted.Succeeded)
        {
            return ApiResults.NoContent();
        }

        return accepted.Error == TenancyErrors.WeakPassword
            ? AccountResults.WeakPassword(accepted.Value!)
            : ApiResults.Error(accepted.Error!);
    }
}
