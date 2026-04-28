using Auth.Server.Providers;
using Auth.Server.Sessions;
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

        // Spec 0010: a private account deletes itself. Behind the access token like the company API; its own path, outside /auth/org.
        app.MapDelete(AccountDeletionEndpoint.Path, AccountDeletionEndpoint.DeleteAsync)
            .RequireAuthorization()
            .WithTags("Account")
            .WithDescription(
                "Deletes the caller's own private account with everything Auth-Core holds of it: the personal space, the sessions, the mail links, "
                + "the queued mails, the pending sign-up and the login streak; the audit rows stay for their retention. Needs the caller's "
                + "confirmation: the password, or for an account without one a provider ticket (below). "
                + "The product deletes its own data under the token's `org_id` first: Auth-Core does not tell it. A token that says `org_kind` "
                + "`personal` while the account has moved to a company or is gone is `403 permissions_changed`; a company member is `403 forbidden` "
                + "(company accounts are managed by their admins). A wrong password counts in the login streak as a failed login does; while the "
                + "identifier is locked the answer is `429 too_many_attempts`. The body holds exactly one of `password` and `provider_ticket` (spec "
                + "0011; both or neither is `400 invalid_request`): an account with a password needs it (`403 password_required` for a ticket), an "
                + "account without one confirms with a provider, by a ticket of a round trip with `purpose=reauth` and the browser's `auth_pt` cookie "
                + "(`403 reauth_required` for a password); a ticket that is unknown, used, expired (5 minutes), of another purpose, without its "
                + "`auth_pt` or of an identity that is not a login of this account is `403 reauth_failed`. `password_required` and `reauth_required` are judged "
                + "before anything is counted and write no row.")
            .ReadsJson<DeleteAccountRequest>()
            .ReadsCookie(ProviderBindingCookie.Name, "With `provider_ticket`: the binding of the ticket, set by the callback that made it.")
            .Produces(StatusCodes.Status204NoContent)
            .SetsRefreshCookie(
                StatusCodes.Status204NoContent,
                $"`{RefreshCookie.Name}` emptied, with `Max-Age=0`: the browser drops the cookie, as at a logout; `{ProviderBindingCookie.Name}` emptied too.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(
                StatusCodes.Status403Forbidden, TenancyErrors.WrongPassword, TenancyErrors.PasswordRequired, TenancyErrors.ReauthRequired,
                TenancyErrors.ReauthFailed)
            .ProducesTooManyAttempts()
            .ProducesGuarded();

        app.MapPost(InviteEndpoints.PreviewPath, InviteEndpoints.PreviewAsync)
            .WithTags("Invitations")
            .WithDescription(
                "The company, the address and the role of an invitation, and `has_private_account`: whether the address has a private account, "
                + "which accepting moves into the company (its personal space is deleted). An address of a company member is `409 already_member`.")
            .ReadsJson<PreviewInviteRequest>()
            .Produces<InvitePreviewResponse>()
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember);
        app.MapPost(InviteEndpoints.AcceptPath, InviteEndpoints.AcceptAsync)
            .WithTags("Invitations")
            .WithDescription(
                "Accepts an invitation. A new account needs `password`. An account that exists keeps its own password (a `password` sent is not "
                + "used); a private account moves only with `leave_private_account: true` (its personal space is deleted, every session ends), "
                + "and without it the answer is `409 private_account_exists` and nothing changes. A company member is `409 already_member`.")
            .ReadsJson<AcceptInviteRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesPasswordError(TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken, TenancyErrors.WeakPassword)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember, TenancyErrors.PrivateAccountExists);

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
                "Deletes the caller's company with its roles, invitations and the mails queued for them, and the account of every member, the "
                + "caller's included: no account is left without a space. Needs the permission `org:delete`, the company's name exactly and the "
                + "caller's own password. An access token still held gets `403 permissions_changed`, the next refresh `401 invalid_grant`, a new "
                + "login the `401` of an unknown address. "
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
            .WithDescription(
                "Removes a member. The account loses its only space and is deleted with its sessions and links; the address can be invited "
                + "again or sign up, as a new account.")
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
