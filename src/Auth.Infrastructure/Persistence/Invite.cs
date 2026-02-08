namespace Auth.Infrastructure.Persistence;

/// <summary>
/// An invitation to join a company with a role (spec 0005 → Invitations). It names an address that may have no account
/// yet, so it cannot live in <see cref="EmailToken"/>, whose rows belong to users. The row exists from the moment the
/// invitation is made; its link token is issued, and its seven days start, when the mail is composed. Until then it has
/// no token, and an expiry set from the time it was made.
/// </summary>
public sealed class Invite
{
    public Guid Id { get; init; }

    public required Guid CompanyId { get; init; }

    /// <summary>The address as the inviter typed it: the account is created with it, and lists show it.</summary>
    public required string Email { get; init; }

    public required string NormalizedEmail { get; init; }

    public required Guid RoleId { get; init; }

    /// <summary>The member who sent it; <see langword="null"/> for the operator.</summary>
    public Guid? InvitedBy { get; init; }

    public required DateTimeOffset InvitedAt { get; init; }

    /// <summary>SHA-256 of the link token; <see langword="null"/> until the mail has been composed.</summary>
    public byte[]? TokenHash { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }
}
