namespace Auth.Server.Tenancy;

// The bodies of the company API (spec 0005 → Contract). Property names become snake_case on the wire.

/// <summary>
/// Response of <c>GET /auth/me</c>: the caller as the database says now. <c>org_kind</c> is <c>organization</c> or <c>personal</c> (spec
/// 0010); a private account's space is named <c>private</c> and its role is <c>private</c>. <c>has_password</c>: the account has a password;
/// <c>linked_providers</c>: the providers of its logins, sorted ordinally (spec 0011).
/// </summary>
public sealed record MeResponse(
    Guid Sub, string Email, Guid OrgId, string OrgName, string OrgKind, string[] Roles, string[] Permissions, bool HasPassword, string[] LinkedProviders);

/// <summary>Response of <c>GET /auth/org</c>.</summary>
public sealed record OrgResponse(Guid Id, string Name);

/// <summary>Response of <c>GET /auth/orgs</c>: every space the caller belongs to.</summary>
public sealed record OrgListResponse(IReadOnlyList<OrgListItem> Orgs);

/// <summary>
/// One space of <c>GET /auth/orgs</c>: <c>kind</c> is <c>organization</c> or <c>personal</c> as <c>org_kind</c> of <c>GET /auth/me</c> has it (a
/// private account's space is named <c>private</c>, as is its role); <c>current</c>: this is the space the token is for.
/// </summary>
public sealed record OrgListItem(Guid Id, string Name, string Kind, RoleRef Role, bool Current);

/// <summary>Request of <c>POST /auth/orgs</c> (spec 0016): the new company's name. Documents the body for OpenAPI; the endpoint reads it itself.</summary>
public sealed record CreateOrgRequest(string Name);

/// <summary>Request of <c>PATCH /auth/org</c>.</summary>
public sealed record RenameOrgRequest(string Name);

/// <summary>Request of <c>DELETE /auth/org</c>: the company's name, exactly, and the caller's own password.</summary>
public sealed record DeleteOrgRequest(string Name, string Password);

/// <summary>
/// Request of <c>DELETE /auth/account</c>: exactly one of the caller's own password (spec 0010) and a re-authentication ticket of a provider
/// (spec 0011); the account decides which it needs.
/// </summary>
public sealed record DeleteAccountRequest(string? Password = null, string? ProviderTicket = null);

/// <summary>A role as a list names it: its id and its name.</summary>
public sealed record RoleRef(Guid Id, string Name);

/// <summary>One pending invitation of <c>GET /auth/org/invites</c>. Times are ISO 8601 in UTC.</summary>
public sealed record InviteItem(Guid Id, string Email, RoleRef Role, DateTime InvitedAt, DateTime ExpiresAt);

/// <summary>Response of <c>GET /auth/org/invites</c>.</summary>
public sealed record InvitesResponse(IReadOnlyList<InviteItem> Invites);

/// <summary>Request of <c>POST /auth/org/invites</c>.</summary>
public sealed record SendInviteRequest(string Email, Guid RoleId);

/// <summary>Request of <c>POST /auth/invites/preview</c>.</summary>
public sealed record PreviewInviteRequest(string Token);

/// <summary>
/// Response of <c>POST /auth/invites/preview</c>; <c>has_private_account</c> (spec 0010): the address has a personal space; <c>has_account</c>
/// (spec 0017): it has an account of any kind; <c>org_id</c>: the invited company.
/// </summary>
public sealed record InvitePreviewResponse(string OrgName, string Email, string Role, bool HasPrivateAccount, bool HasAccount, Guid OrgId);

/// <summary>
/// Request of <c>POST /auth/invites/accept</c>: the password of a new account (not used for an account that exists), and
/// <c>leave_private_account</c>, a JSON boolean, with which a private account moves into the company on purpose (spec 0010; since spec 0017
/// a private account joins without it and keeps its personal space).
/// </summary>
public sealed record AcceptInviteRequest(string Token, string? Password = null, bool? LeavePrivateAccount = null);

/// <summary>One member of <c>GET /auth/org/members</c>. The time is ISO 8601 in UTC.</summary>
public sealed record MemberItem(Guid UserId, string Email, RoleRef Role, DateTime JoinedAt);

/// <summary>Response of <c>GET /auth/org/members</c>.</summary>
public sealed record MembersResponse(IReadOnlyList<MemberItem> Members);

/// <summary>Request of <c>PUT /auth/org/members/{user_id}/role</c>.</summary>
public sealed record ChangeMemberRoleRequest(Guid RoleId);

/// <summary>
/// A role of <c>GET /auth/org/roles</c>, and the answer of <c>POST /auth/org/roles</c>: the permissions it holds that are
/// still in the catalog (<c>*</c> is shown as it is), and how many members hold it.
/// </summary>
public sealed record RoleItem(Guid Id, string Name, string[] Permissions, int Members);

/// <summary>Response of <c>GET /auth/org/roles</c>: the roles, and every permission a role may hold, <c>*</c> first.</summary>
public sealed record RolesResponse(IReadOnlyList<RoleItem> Roles, IReadOnlyList<string> Catalog);

/// <summary>Request of <c>POST /auth/org/roles</c> and <c>PUT /auth/org/roles/{id}</c>.</summary>
public sealed record SaveRoleRequest(string Name, string[] Permissions);
