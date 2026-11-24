namespace Auth.Infrastructure.Persistence;

/// <summary>
/// A user's membership in a space, with exactly one role. A user belongs to several companies and to at most one
/// personal space; every account belongs to at least one space.
/// </summary>
public sealed class Membership
{
    public required Guid UserId { get; init; }

    public required Guid CompanyId { get; init; }

    public required Guid RoleId { get; set; }

    public required DateTimeOffset JoinedAt { get; init; }

    /// <summary>
    /// When a sign-in or a switch last chose this membership (spec 0015); <see langword="null"/> for a membership no sign-in or
    /// switch has chosen yet, which is every membership that exists before this column is written. A refresh never writes it.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
