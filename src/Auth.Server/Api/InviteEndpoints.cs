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
    private static readonly string[] AcceptFields = ["token", "password"];

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
            ? ApiResults.Ok(new InvitePreviewResponse(preview.Value!.OrgName, preview.Value.Email, preview.Value.Role))
            : ApiResults.Error(preview.Error!);
    }

    public static async Task<IResult> AcceptAsync(HttpContext http, InviteAcceptance acceptance)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(acceptance);

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, AcceptFields, http.RequestAborted);
        if (fields is null || fields[1].Contains('\0'))
        {
            return ApiResults.InvalidRequest();
        }

        var accepted = await acceptance.AcceptAsync(fields[0], fields[1], http.RequestAborted);
        if (accepted.Succeeded)
        {
            return ApiResults.NoContent();
        }

        return accepted.Error == TenancyErrors.WeakPassword
            ? AccountResults.WeakPassword(accepted.Value!)
            : ApiResults.Error(accepted.Error!);
    }
}
