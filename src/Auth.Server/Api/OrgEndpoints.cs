using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Api;

/// <summary>
/// <c>GET /auth/me</c>, <c>GET /auth/org</c> and <c>PATCH /auth/org</c> (spec 0005 → Company API): who the caller is,
/// which company, and its name. Every answer is read from the database, never from the token.
/// </summary>
public static class OrgEndpoints
{
    public const string MePath = "/auth/me";
    public const string OrgPath = "/auth/org";

    private static readonly string[] RenameFields = ["name"];

    public static async Task<IResult> MeAsync(HttpContext http, MembershipReader memberships, UserManager<ApplicationUser> users)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(users);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        var user = await users.FindByIdAsync(caller.UserId.ToString());
        if (user?.Email is null)
        {
            return ApiResults.Error(TenancyErrors.Forbidden);
        }

        return ApiResults.Ok(new MeResponse(caller.UserId, user.Email, caller.CompanyId, caller.CompanyName, [caller.RoleName], [.. caller.Permissions]));
    }

    public static async Task<IResult> GetAsync(HttpContext http, MembershipReader memberships)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships);
        return access.Caller is { } caller
            ? ApiResults.Ok(new OrgResponse(caller.CompanyId, caller.CompanyName))
            : access.Failure!;
    }

    public static async Task<IResult> RenameAsync(HttpContext http, MembershipReader memberships, AuthDbContext db)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(db);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.OrgManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, RenameFields, http.RequestAborted);
        if (fields is null || !NameInput.IsValid(fields[0]))
        {
            return ApiResults.InvalidRequest();
        }

        var name = fields[0];
        await db.Companies.Where(c => c.Id == caller.CompanyId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, name), http.RequestAborted);
        return ApiResults.NoContent();
    }
}
