namespace Auth.Server.Audit;

/// <summary>The kinds of the audit log (spec 0008 → Audit log). The names are the contract's; the runbook queries them.</summary>
public static class AuditKinds
{
    public const string LoginSucceeded = "login.succeeded";
    public const string LoginFailed = "login.failed";
    public const string LoginLocked = "login.locked";
    public const string Logout = "logout";
    public const string RefreshReuseDetected = "refresh.reuse_detected";
    public const string PasswordResetRequested = "password.reset_requested";
    public const string PasswordReset = "password.reset";
    public const string EmailVerified = "email.verified";
    public const string InviteSent = "invite.sent";
    public const string InviteResent = "invite.resent";
    public const string InviteAccepted = "invite.accepted";
    public const string InviteCancelled = "invite.cancelled";
    public const string MemberRemoved = "member.removed";
    public const string MemberRoleChanged = "member.role_changed";
    public const string RoleCreated = "role.created";
    public const string RoleUpdated = "role.updated";
    public const string RoleDeleted = "role.deleted";
    public const string OrgCreated = "org.created";
    public const string OrgRenamed = "org.renamed";
    public const string OrgDeleted = "org.deleted";
    public const string OrgDeleteRefused = "org.delete_refused";
    public const string RateLimitHit = "rate_limit.hit";

    public static IReadOnlyList<string> All { get; } =
    [
        LoginSucceeded, LoginFailed, LoginLocked, Logout, RefreshReuseDetected,
        PasswordResetRequested, PasswordReset, EmailVerified,
        InviteSent, InviteResent, InviteAccepted, InviteCancelled,
        MemberRemoved, MemberRoleChanged, RoleCreated, RoleUpdated, RoleDeleted,
        OrgCreated, OrgRenamed, OrgDeleted, OrgDeleteRefused, RateLimitHit,
    ];
}
