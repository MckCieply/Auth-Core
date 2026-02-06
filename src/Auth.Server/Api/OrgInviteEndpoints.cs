using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Api;

/// <summary>
/// The invitations of the caller's company (spec 0005 → Company API): list, send, resend, cancel. All need
/// <c>members:manage</c>, checked against the database.
/// </summary>
public static class OrgInviteEndpoints
{
    private static readonly string[] SendFields = ["email", "role_id"];

    public static async Task<IResult> ListAsync(HttpContext http, MembershipReader memberships, InvitationService invitations)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(invitations);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        return access.Caller is { } caller
            ? ApiResults.Ok(new InvitesResponse(await invitations.ListAsync(caller.CompanyId, http.RequestAborted)))
            : access.Failure!;
    }

    public static async Task<IResult> SendAsync(
        HttpContext http, MembershipReader memberships, InvitationService invitations, ILookupNormalizer normalizer, MailDispatchSignal signal)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(signal);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, SendFields, http.RequestAborted);
        if (fields is null
            || fields[0].Length > EmailInput.MaxLength
            || !EmailInput.IsMailbox(fields[0])
            || !EmailInput.TryNormalize(fields[0], normalizer, out var normalized)
            || !IdInput.TryParse(fields[1], out var roleId))
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await invitations.SendAsync(Actor.Of(caller), caller.CompanyId, fields[0], normalized, roleId, http.RequestAborted);
        if (!outcome.Succeeded)
        {
            return ApiResults.Refused(outcome);
        }

        signal.Notify();
        return ApiResults.Accepted();
    }

    public static async Task<IResult> ResendAsync(
        string id, HttpContext http, MembershipReader memberships, InvitationService invitations, MailDispatchSignal signal)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(signal);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        if (!IdInput.TryParse(id, out var inviteId))
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await invitations.ResendAsync(Actor.Of(caller), caller.CompanyId, inviteId, http.RequestAborted);
        if (!outcome.Succeeded)
        {
            return ApiResults.Refused(outcome);
        }

        signal.Notify();
        return ApiResults.Accepted();
    }

    public static async Task<IResult> CancelAsync(string id, HttpContext http, MembershipReader memberships, InvitationService invitations)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(invitations);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        if (!IdInput.TryParse(id, out var inviteId))
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await invitations.CancelAsync(Actor.Of(caller), caller.CompanyId, inviteId, http.RequestAborted);
        return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
    }
}
