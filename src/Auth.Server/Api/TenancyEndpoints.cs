using Auth.Server.Providers;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

public static class TenancyEndpoints
{
    /// <summary>The sentence of every company operation that has no other description (spec 0015, criterion 33).</summary>
    private const string ActsOnTokenCompany = "Acts on the company of the token (its `org_id`), whichever company a sign-in would choose today.";

    /// <summary>
    /// The endpoints of spec 0005: the caller's own data and the company API. <c>GET /auth/me</c> and everything under <c>/auth/org</c> act on
    /// the token's company (its <c>org_id</c>), checked against the database on every call (spec 0015), and need an access token; the
    /// invitation endpoints that need none are mapped beside it. <c>GET /auth/orgs</c> does not act on the token's company: it lists every
    /// space of the account, so it deliberately does not go through <see cref="CompanyAccess"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapTenancyApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(OrgEndpoints.MePath, OrgEndpoints.MeAsync)
            .RequireAuthorization()
            .WithTags("Company")
            .WithDescription(
                "The caller as the database says now, in the token's company (its `org_id`): the membership, role and permissions there, whichever "
                + "company a sign-in would choose today. When the account is no longer a member of the token's company the answer is `403 permissions_changed`. "
                + "`confirmed_until` is the window of this device's session (spec 0012), null when it is closed or the session is unknown.")
            .ReadsRefreshCookie(
                "The session of this device, for `confirmed_until`; read, never redeemed. Missing, or of no session of this account: "
                + "`confirmed_until` is null.")
            .Produces<MeResponse>()
            .ProducesGuarded();

        // Spec 0015: the spaces of the account, behind the access token only: it answers when the token's own company is gone.
        app.MapGet(OrgListEndpoint.Path, OrgListEndpoint.ListAsync)
            .RequireAuthorization()
            .WithTags("Company")
            .WithDescription(
                "Every space the caller belongs to, as the database says now: companies and a personal space (`kind` `organization` or `personal`, "
                + "as `org_kind` of `GET /auth/me`), each with the caller's role there. Ordered by `name`, ignoring case, then by `id`. `current` marks "
                + "the space the token is for (its `org_id`); no entry is current when the account is no longer a member of it. An account that is gone "
                + "or has no membership gets `403 permissions_changed`.")
            .Produces<OrgListResponse>()
            .ProducesGuarded();

        // Spec 0016: a signed-in account creates another company. Behind the access token only, as the list; the same path, so that a 405 names both.
        app.MapPost(OrgCreateEndpoint.Path, OrgCreateEndpoint.CreateAsync)
            .RequireAuthorization()
            .WithTags("Company")
            .WithDescription(
                "Creates another company for the caller, who becomes its first admin (the first role of the manifest's `default_roles` that holds `*`, "
                + "else the first that holds `members:manage`), when the product's manifest says `org_creation: self-service` (`GET /auth/options`); "
                + "any account may create, a private one included, and its personal space stays. The body is `{\"name\"}`: 1 to 100 characters, no "
                + "control, format or noncharacters, no leading or trailing space; names are not unique. The checks run in this order: no valid access "
                + "token `401`; creation off `403 org_creation_disabled` (before the body is read); a body or name that breaks the rules `400 "
                + "invalid_request`; an account that is gone or has no membership `403 permissions_changed` (the token's own company need not still "
                + "be one); the account already in `Auth:Tenancy:MaxCompaniesPerAccount` companies (the personal space is not counted) `409 "
                + "company_limit_reached`. The answer is `201` with the new space as `GET /auth/orgs` lists it, `current` always `false`. The session "
                + "does not change (no cookie is set, the token and the refresh cookie keep their company): the product calls `POST /auth/switch` with "
                + "the new `id` to go into it. A refused creation writes nothing.")
            .ReadsJson<CreateOrgRequest>()
            .Produces<OrgListItem>(StatusCodes.Status201Created)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.OrgCreationDisabled)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.CompanyLimitReached)
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
                + "`personal` while the account has no membership in the token's company any more (it moved to a company, or is gone) is "
                + "`403 permissions_changed`. Only an account whose only membership is its personal space may delete itself: an account that "
                + "holds a personal space and at least one company, whichever space the token names, is `403 company_member` (it deletes its "
                + "companies or has their admins remove it first); an account with companies only is `403 forbidden` (company accounts are "
                + "managed by their admins). These are judged on the database before the body is read and again under the lock, and nothing is "
                + "counted, no password is evaluated and no row is written. A wrong password counts in the login streak as a failed login does; while the "
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
                TenancyErrors.ReauthFailed, TenancyErrors.CompanyMember)
            .ProducesTooManyAttempts()
            .ProducesGuarded();

        app.MapPost(InviteEndpoints.PreviewPath, InviteEndpoints.PreviewAsync)
            .WithTags("Invitations")
            .WithDescription(
                "The company (`org_name`, and `org_id`, the company to pass to `POST /auth/switch` after the acceptance), the address and the role of an "
                + "invitation; `has_account`: whether the address has an account of any kind (a screen asks for a password only when it is `false`); "
                + "and `has_private_account`: whether that account has a personal space, also when it holds a company too (accepting with "
                + "`leave_private_account: true` deletes the space). `409 already_member` is answered only for an account that is a member of this "
                + "company. The preview never uses the invitation.")
            .ReadsJson<PreviewInviteRequest>()
            .Produces<InvitePreviewResponse>()
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember);
        app.MapPost(InviteEndpoints.AcceptPath, InviteEndpoints.AcceptAsync)
            .WithTags("Invitations")
            .WithDescription(
                "Accepts an invitation. A new account needs `password`. An account that exists joins with the token alone: it keeps its own "
                + "password (a `password` sent is not used or judged), its other memberships, its personal space and its linked providers, and no "
                + "session ends. `leave_private_account: true` also deletes the account's personal space (a move; sessions of that space get "
                + "`401 invalid_grant` at their next refresh or switch, every other session goes on); without a personal space it is ignored. A "
                + "member of the invited company is `409 already_member` and the invitation stays usable.")
            .ReadsJson<AcceptInviteRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesPasswordError(TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken, TenancyErrors.WeakPassword)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember);

        var company = app.MapGroup(OrgEndpoints.OrgPath).RequireAuthorization().WithTags("Company");

        // Every operation of this group acts on the token's company (its `org_id`), whichever company a sign-in would choose today.
        company.MapGet("", OrgEndpoints.GetAsync)
            .WithDescription("The token's company (its `org_id`). An account that is no longer a member of it gets `403 permissions_changed`.")
            .Produces<OrgResponse>()
            .ProducesGuarded();
        company.MapPatch("", OrgEndpoints.RenameAsync)
            .WithDescription("Renames the token's company (its `org_id`). " + ActsOnTokenCompany)
            .ReadsJson<RenameOrgRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesGuarded();
        company.MapDelete("", OrgEndpoints.DeleteAsync)
            .WithDescription(
                "Deletes the token's company (its `org_id`), its memberships, roles and invitations and the mails queued for them, and the account of "
                + "every member who has no other space (the caller's included when this was their last). Members with another space keep their "
                + "account; their sessions in this company get `401` at the next refresh, and their next login lands in another space. Needs the "
                + "permission `org:delete`, the company's name exactly and the caller's confirmation (password or provider ticket). An access token still held gets "
                + "`403 permissions_changed`, the next refresh `401 invalid_grant`, and a new login by an account that is gone the `401` of an "
                + "unknown address. "
                + "A wrong password counts in the caller's login streak as a failed login does; while the identifier is locked the answer is `429 too_many_attempts`. "
                + "The body holds the name and exactly one of `password` and `provider_ticket` (spec 0012; both, neither or a member of the wrong "
                + "type is `400 invalid_request`). An admin with a password confirms with it (`403 password_required` for a ticket); an admin whose "
                + "account has no password (made through a provider) confirms with a ticket of a round trip with `purpose=reauth` and the browser's "
                + "`auth_pt` (`403 reauth_required` for a password); neither is counted or written. A ticket that is unknown, used, expired (5 "
                + "minutes), of another purpose, without its `auth_pt` or of an identity that is not a login of the caller is `403 reauth_failed`, "
                + "recorded, and nothing is deleted; it is used up inside the deletion, so a deletion refused afterwards keeps it, and the answer "
                + "clears `auth_pt`.")
            .ReadsJson<DeleteOrgRequest>()
            .ReadsCookie(ProviderBindingCookie.Name, "With `provider_ticket`: the binding of the ticket, set by the callback that made it.")
            .Produces(StatusCodes.Status204NoContent)
            .SetsHeader(StatusCodes.Status204NoContent, "Set-Cookie", $"With `provider_ticket`: `{ProviderBindingCookie.Name}` emptied, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(
                StatusCodes.Status403Forbidden, TenancyErrors.WrongPassword, TenancyErrors.PermissionNotHeld, TenancyErrors.ReauthRequired,
                TenancyErrors.PasswordRequired, TenancyErrors.ReauthFailed)
            .ProducesTooManyAttempts()
            .ProducesGuarded();

        company.MapGet("invites", OrgInviteEndpoints.ListAsync)
            .WithDescription("The pending invitations of the token's company. " + ActsOnTokenCompany)
            .Produces<InvitesResponse>()
            .ProducesGuarded();
        company.MapPost("invites", OrgInviteEndpoints.SendAsync)
            .WithDescription("Invites an address to the token's company. " + ActsOnTokenCompany)
            .ReadsJson<SendInviteRequest>()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyInOrg, TenancyErrors.InvitePending)
            .ProducesTooManyAttempts()
            .ProducesGuarded();
        company.MapPost("invites/{id}/resend", OrgInviteEndpoints.ResendAsync)
            .WithDescription("Sends a pending invitation of the token's company again. " + ActsOnTokenCompany)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesTooManyAttempts()
            .ProducesGuarded();
        company.MapDelete("invites/{id}", OrgInviteEndpoints.CancelAsync)
            .WithDescription("Cancels a pending invitation of the token's company. " + ActsOnTokenCompany)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesGuarded();

        company.MapGet("members", OrgMemberEndpoints.ListAsync)
            .WithDescription("The members of the token's company. " + ActsOnTokenCompany)
            .Produces<MembersResponse>()
            .ProducesGuarded();
        company.MapPut("members/{user_id}/role", OrgMemberEndpoints.ChangeRoleAsync)
            .WithDescription("Changes the role of a member of the token's company. " + ActsOnTokenCompany)
            .ReadsJson<ChangeMemberRoleRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.CannotChangeSelf, TenancyErrors.LastManager)
            .ProducesGuarded();
        company.MapDelete("members/{user_id}", OrgMemberEndpoints.RemoveAsync)
            .WithDescription(
                "Removes a member of the token's company (its `org_id`). The membership is removed. When it was the account's last space, the "
                + "account is deleted with its sessions and links (the address can then be invited again or sign up, as a new account); otherwise "
                + "the account stays and only its sessions in this company get `401` at the next refresh.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.CannotChangeSelf, TenancyErrors.LastManager)
            .ProducesGuarded();

        company.MapGet("roles", OrgRoleEndpoints.ListAsync)
            .WithDescription("The roles of the token's company and the catalog of permissions. " + ActsOnTokenCompany)
            .Produces<RolesResponse>()
            .ProducesGuarded();
        company.MapPost("roles", OrgRoleEndpoints.CreateAsync)
            .WithDescription("Creates a role in the token's company. " + ActsOnTokenCompany)
            .ReadsJson<SaveRoleRequest>()
            .Produces<RoleItem>(StatusCodes.Status201Created)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.UnknownPermission)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.RoleNameTaken)
            .ProducesGuarded();
        company.MapPut("roles/{id}", OrgRoleEndpoints.ReplaceAsync)
            .WithDescription("Replaces a role of the token's company. " + ActsOnTokenCompany)
            .ReadsJson<SaveRoleRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.UnknownPermission)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.RoleNameTaken, TenancyErrors.LastManager)
            .ProducesGuarded();
        company.MapDelete("roles/{id}", OrgRoleEndpoints.DeleteAsync)
            .WithDescription("Deletes a role of the token's company. " + ActsOnTokenCompany)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.RoleInUse)
            .ProducesGuarded();

        return app;
    }
}
