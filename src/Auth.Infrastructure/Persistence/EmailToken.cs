namespace Auth.Infrastructure.Persistence;

/// <summary>
/// A link token sent in a mail (spec 0004). Only its hash is stored; the token in clear exists in the mail alone.
/// There is one row per user and kind: a new token overwrites it, and it is deleted when the token is used or expired.
/// </summary>
public sealed class EmailToken
{
    /// <summary>SHA-256 of the token as it appears in the link: 32 bytes.</summary>
    public required byte[] TokenHash { get; init; }

    public required Guid UserId { get; init; }

    public required MailKind Kind { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}
