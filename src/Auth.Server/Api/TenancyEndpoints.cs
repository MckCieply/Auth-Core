using Auth.Server.Tenancy;

namespace Auth.Server.Api;

public static class TenancyEndpoints
{
    /// <summary>
    /// The endpoints of spec 0005: the caller's own data and the company API. Everything under <c>/auth/org</c> acts on
    /// the caller's company, taken from the database, and needs an access token; the invitation endpoints that need
    /// none are mapped beside it.
    /// </summary>
    public static IEndpointRouteBuilder MapTenancyApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(OrgEndpoints.MePath, OrgEndpoints.MeAsync)
            .RequireAuthorization()
            .WithTags("Company")
            .Produces<MeResponse>()
            .ProducesGuarded();

        app.MapPost(InviteEndpoints.PreviewPath, InviteEndpoints.PreviewAsync)
            .WithTags("Invitations")
            .ReadsJson<PreviewInviteRequest>()
            .Produces<InvitePreviewResponse>()
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember);
        app.MapPost(InviteEndpoints.AcceptPath, InviteEndpoints.AcceptAsync)
            .WithTags("Invitations")
            .ReadsJson<AcceptInviteRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesPasswordError(TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken, TenancyErrors.WeakPassword)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember);

        var company = app.MapGroup(OrgEndpoints.OrgPath).RequireAuthorization().WithTags("Company");

        company.MapGet("", OrgEndpoints.GetAsync)
            .Produces<OrgResponse>()
            .ProducesGuarded();
        company.MapPatch("", OrgEndpoints.RenameAsync)
            .ReadsJson<RenameOrgRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesGuarded();
        company.MapDelete("", OrgEndpoints.DeleteAsync)
            .WithDescription(
                "Deletes the caller's company with its roles, members, invitations and the mails queued for them; the accounts stay. "
                + "Needs the permission `org:delete`, the company's name exactly and the caller's own password. The members' sessions end: "
                + "an access token still held gets `403 permissions_changed`, the next refresh `401 invalid_grant`, a new login `403 no_membership`. "
                + "A wrong password counts in the caller's login streak as a failed login does; while the identifier is locked the answer is `429 too_many_attempts`.")
            .ReadsJson<DeleteOrgRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.WrongPassword, TenancyErrors.PermissionNotHeld)
            .ProducesTooManyAttempts()
            .ProducesGuarded();

        company.MapGet("invites", OrgInviteEndpoints.ListAsync)
            .Produces<InvitesResponse>()
            .ProducesGuarded();
        company.MapPost("invites", OrgInviteEndpoints.SendAsync)
            .ReadsJson<SendInviteRequest>()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyInOrg, TenancyErrors.InvitePending)
            .ProducesTooManyAttempts()
            .ProducesGuarded();
        company.MapPost("invites/{id}/resend", OrgInviteEndpoints.ResendAsync)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesTooManyAttempts()
            .ProducesGuarded();
        company.MapDelete("invites/{id}", OrgInviteEndpoints.CancelAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesGuarded();

        company.MapGet("members", OrgMemberEndpoints.ListAsync)
            .Produces<MembersResponse>()
            .ProducesGuarded();
        company.MapPut("members/{user_id}/role", OrgMemberEndpoints.ChangeRoleAsync)
            .ReadsJson<ChangeMemberRoleRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.CannotChangeSelf, TenancyErrors.LastManager)
            .ProducesGuarded();
        company.MapDelete("members/{user_id}", OrgMemberEndpoints.RemoveAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.CannotChangeSelf, TenancyErrors.LastManager)
            .ProducesGuarded();

        company.MapGet("roles", OrgRoleEndpoints.ListAsync)
            .Produces<RolesResponse>()
            .ProducesGuarded();
        company.MapPost("roles", OrgRoleEndpoints.CreateAsync)
            .ReadsJson<SaveRoleRequest>()
            .Produces<RoleItem>(StatusCodes.Status201Created)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.UnknownPermission)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.RoleNameTaken)
            .ProducesGuarded();
        company.MapPut("roles/{id}", OrgRoleEndpoints.ReplaceAsync)
            .ReadsJson<SaveRoleRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.UnknownPermission)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.RoleNameTaken, TenancyErrors.LastManager)
            .ProducesGuarded();
        company.MapDelete("roles/{id}", OrgRoleEndpoints.DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.RoleInUse)
            .ProducesGuarded();

        return app;
    }
}
