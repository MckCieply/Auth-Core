namespace Auth.Infrastructure.Persistence;

/// <summary>
/// One row of the audit log (spec 0008 → Audit log): who did what, to whom, in which company, from which address, when. There are
/// no foreign keys: a row outlives the account, company, role or invitation it names. A password, a token, a link, a cookie or a
/// mail body is never recorded. Written by Auth-Core only, read by SQL.
/// </summary>
public sealed class AuditEvent
{
    public required Guid Id { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>One of the kinds of the contract, for example <c>login.failed</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The account that acted; null for the operator CLI, an anonymous request or a failed login.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>The account acted on, when there is one.</summary>
    public Guid? SubjectUserId { get; init; }

    /// <summary>The address involved, as stored or (a failed login of an unknown address) as typed; at most 256 characters.</summary>
    public string? SubjectEmail { get; init; }

    public Guid? OrgId { get; init; }

    /// <summary>The company's name at that moment.</summary>
    public string? OrgName { get; init; }

    /// <summary>The role or the invitation involved.</summary>
    public Guid? TargetId { get; init; }

    /// <summary>The client address, whole; <c>unknown</c> when the request had none; null for the operator CLI.</summary>
    public string? ClientIp { get; init; }

    /// <summary>A small JSON object: the reason of a failure, the old and the new role, <c>"via": "cli"</c>.</summary>
    public string? Details { get; init; }
}
