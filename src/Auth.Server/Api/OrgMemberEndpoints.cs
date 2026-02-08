using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Auth.Server.Api;

/// <summary>
/// The members of the caller's company (spec 0005 → Company API): list, change a role, remove. All need
/// <c>members:manage</c>, checked against the database.
/// </summary>
public static class OrgMemberEndpoints
{
    private static readonly string[] RoleFields = ["role_id"];

    public static async Task<IResult> ListAsync(HttpContext http, MembershipReader memberships, MemberService members)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(members);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        return access.Caller is { } caller
            ? ApiResults.Ok(new MembersResponse(await members.ListAsync(caller.CompanyId, http.RequestAborted)))
            : access.Failure!;
    }

    public static async Task<IResult> ChangeRoleAsync([FromRoute(Name = "user_id")] string userId, HttpContext http, MembershipReader memberships, MemberService members)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(members);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, RoleFields, http.RequestAborted);
        if (!IdInput.TryParse(userId, out var user) || fields is null || !IdInput.TryParse(fields[0], out var role))
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await members.ChangeRoleAsync(Actor.Of(caller), caller.CompanyId, user, role, http.RequestAborted);
        return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
    }

    public static async Task<IResult> RemoveAsync([FromRoute(Name = "user_id")] string userId, HttpContext http, MembershipReader memberships, MemberService members)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(members);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        if (!IdInput.TryParse(userId, out var user))
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await members.RemoveAsync(Actor.Of(caller), caller.CompanyId, user, force: false, http.RequestAborted);
        return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
    }
}
