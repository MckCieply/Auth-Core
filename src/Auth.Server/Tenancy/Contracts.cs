namespace Auth.Server.Tenancy;

// The bodies of the company API (spec 0005 → Contract). Property names become snake_case on the wire.

/// <summary>Response of <c>GET /auth/me</c>: the caller as the database says now.</summary>
public sealed record MeResponse(Guid Sub, string Email, Guid OrgId, string OrgName, string[] Roles, string[] Permissions);

/// <summary>Response of <c>GET /auth/org</c>.</summary>
public sealed record OrgResponse(Guid Id, string Name);

/// <summary>Request of <c>PATCH /auth/org</c>.</summary>
public sealed record RenameOrgRequest(string Name);
