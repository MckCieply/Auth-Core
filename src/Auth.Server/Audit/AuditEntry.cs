namespace Auth.Server.Audit;

/// <summary>What a flow says about an event; <see cref="AuditLog"/> adds the time, the id, the client address and the operator mark.</summary>
public sealed class AuditEntry
{
    public required string Kind { get; init; }

    /// <summary>The account that acted. Left null for an anonymous request, a failed login and the operator; a member's id comes from the actor.</summary>
    public Guid? ActorUserId { get; init; }

    public Guid? SubjectUserId { get; init; }

    /// <summary>The address involved: as stored, or as typed for an address nobody has. Never a password, a token or a link.</summary>
    public string? SubjectEmail { get; init; }

    public Guid? OrgId { get; init; }

    public string? OrgName { get; init; }

    public Guid? TargetId { get; init; }

    /// <summary>Small values only: names, reasons, counts. Never a secret.</summary>
    public IReadOnlyDictionary<string, object?>? Details { get; init; }
}
