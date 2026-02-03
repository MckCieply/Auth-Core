namespace Auth.Infrastructure.Persistence;

/// <summary>
/// A user's membership in a company, with exactly one role. The key allows a user to belong to several companies,
/// so that switching companies later is additive; this version lets a user belong to one, and the code keeps it so.
/// </summary>
public sealed class Membership
{
    public required Guid UserId { get; init; }

    public required Guid CompanyId { get; init; }

    public required Guid RoleId { get; set; }

    public required DateTimeOffset JoinedAt { get; init; }
}
