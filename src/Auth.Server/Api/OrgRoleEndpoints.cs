using Auth.Server.Requests;
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

/// <summary>
/// The roles of the caller's company (spec 0005 → Company API): list, create, replace, delete. Listing is open to
/// <c>roles:manage</c> and to <c>members:manage</c>, because inviting needs the list; the rest need <c>roles:manage</c>.
/// All are checked against the database.
/// </summary>
public static class OrgRoleEndpoints
{
    public static async Task<IResult> ListAsync(HttpContext http, MembershipReader memberships, RoleService roles)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(roles);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.RolesManage, PermissionCatalog.MembersManage);
        return access.Caller is { } caller
            ? ApiResults.Ok(await roles.ListAsync(caller.CompanyId, http.RequestAborted))
            : access.Failure!;
    }

    public static async Task<IResult> CreateAsync(HttpContext http, MembershipReader memberships, RoleService roles)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(roles);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.RolesManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        if (await RoleBodyReader.ReadAsync(http.Request, http.RequestAborted) is not { } body)
        {
            return ApiResults.InvalidRequest();
        }

        var created = await roles.CreateAsync(Actor.Of(caller), caller.CompanyId, body.Name, body.Permissions, http.RequestAborted);
        return created.Succeeded ? ApiResults.Created(created.Value!) : ApiResults.Refused(created.Without);
    }

    public static async Task<IResult> ReplaceAsync(string id, HttpContext http, MembershipReader memberships, RoleService roles)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(roles);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.RolesManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        if (!IdInput.TryParse(id, out var roleId) || await RoleBodyReader.ReadAsync(http.Request, http.RequestAborted) is not { } body)
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await roles.UpdateAsync(Actor.Of(caller), caller.CompanyId, roleId, body.Name, body.Permissions, http.RequestAborted);
        return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
    }

    public static async Task<IResult> DeleteAsync(string id, HttpContext http, MembershipReader memberships, RoleService roles)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(roles);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.RolesManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        if (!IdInput.TryParse(id, out var roleId))
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await roles.DeleteAsync(Actor.Of(caller), caller.CompanyId, roleId, http.RequestAborted);
        return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
    }
}
