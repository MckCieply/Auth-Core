namespace Auth.Server.Tenancy;

// The bodies of the company API (spec 0005 → Contract). Property names become snake_case on the wire.

/// <summary>Response of <c>GET /auth/me</c>: the caller as the database says now.</summary>
public sealed record MeResponse(Guid Sub, string Email, Guid OrgId, string OrgName, string[] Roles, string[] Permissions);

/// <summary>Response of <c>GET /auth/org</c>.</summary>
public sealed record OrgResponse(Guid Id, string Name);

/// <summary>Request of <c>PATCH /auth/org</c>.</summary>
public sealed record RenameOrgRequest(string Name);

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

/// <summary>Response of <c>POST /auth/invites/preview</c>.</summary>
public sealed record InvitePreviewResponse(string OrgName, string Email, string Role);

/// <summary>Request of <c>POST /auth/invites/accept</c>.</summary>
public sealed record AcceptInviteRequest(string Token, string Password);
