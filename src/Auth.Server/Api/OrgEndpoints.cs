using Auth.Infrastructure.Identity;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Api;

/// <summary>
/// <c>GET /auth/me</c>, <c>GET /auth/org</c>, <c>PATCH /auth/org</c> (spec 0005 → Company API) and <c>DELETE /auth/org</c>
/// (spec 0008): who the caller is, which company, its name, and its deletion. Every answer is read from the database, never
/// from the token.
/// </summary>
public static class OrgEndpoints
{
    public const string MePath = "/auth/me";
    public const string OrgPath = "/auth/org";

    private static readonly string[] RenameFields = ["name"];
    private static readonly string[] DeleteFields = ["name", "password"];

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

    public static async Task<IResult> RenameAsync(HttpContext http, MembershipReader memberships, CompanyService companies)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(companies);

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

        // Under the company lock, with the caller read again: they may have lost org:manage since the check above.
        var outcome = await companies.RenameAsync(Actor.Of(caller), caller.CompanyId, fields[0], http.RequestAborted);
        return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
    }

    /// <summary>
    /// <c>DELETE /auth/org</c> (spec 0008 → Company deletion). The checks run in this order: the permission <c>org:delete</c> (read from
    /// the database), the body, the lockout of the caller's identifier, the password, the company's name, safety rule 1. The attempt
    /// is counted first, as login counts it, and a correct password ends the streak: the password is evaluated once.
    /// </summary>
    public static async Task<IResult> DeleteAsync(
        HttpContext http, MembershipReader memberships, UserManager<ApplicationUser> users, LoginStreakStore streaks,
        CompanyDeletionService deletion, AuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(deletion);
        ArgumentNullException.ThrowIfNull(audit);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.OrgDelete);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, DeleteFields, http.RequestAborted);
        if (fields is null || fields[1].Contains('\0'))
        {
            return ApiResults.InvalidRequest();
        }

        var user = await users.FindByIdAsync(caller.UserId.ToString());
        if (user?.Email is null)
        {
            return ApiResults.Error(TenancyErrors.Forbidden);
        }

        var identifier = LoginIdentifier.HashOf(users.NormalizeEmail(user.Email));
        var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
        if (!decision.Allowed)
        {
            await RecordRefusalAsync(audit, caller, "locked");
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        if (!await users.CheckPasswordAsync(user, fields[1]))
        {
            await RecordRefusalAsync(audit, caller, "wrong_password");
            return ApiResults.Error(TenancyErrors.WrongPassword);
        }

        // Not the request's token: from the tenth attempt on, counting has already started a cooldown, and a client that goes away
        // right after its correct password was verified must not be left with it (as login does).
        await streaks.ClearAsync(identifier, CancellationToken.None);

        var outcome = await deletion.DeleteAsync(Actor.Of(caller), caller.CompanyId, fields[0], http.RequestAborted);
        if (outcome.Succeeded)
        {
            return ApiResults.NoContent();
        }

        // A company that has vanished while this request waited for its lock is a membership that is gone: the token no longer
        // matches the database, which is what the contract says for that case.
        return outcome.Error == TenancyErrors.NotFound ? ApiResults.Error(TenancyErrors.PermissionsChanged) : ApiResults.Refused(outcome);
    }

    private static Task RecordRefusalAsync(AuditLog audit, TenantContext caller, string reason) =>
        audit.WriteAloneAsync(
            new AuditEntry
            {
                Kind = AuditKinds.OrgDeleteRefused,
                OrgId = caller.CompanyId,
                OrgName = caller.CompanyName,
                Details = new Dictionary<string, object?> { ["reason"] = reason },
            },
            Actor.Of(caller));
}
