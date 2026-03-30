namespace Auth.Infrastructure.Persistence;

/// <summary>
/// A sign-up that has been asked for and not finished (spec 0009 → Pending sign-ups). It is not an account: login, <c>forgot password</c> and
/// invitations do not see it, and it has no foreign key. There is one per normalised address; a new request replaces it.
/// <see cref="ExpiresAt"/> is when the row goes (24 hours after the request, and after the mail that carries its link);
/// <see cref="TokenExpiresAt"/> is when the current link stops working (24 hours after that mail), and is never later than
/// <see cref="ExpiresAt"/>.
/// </summary>
public sealed class PendingSignup
{
    /// <summary>The address as the account lookup normalises it: the key.</summary>
    public required string NormalizedEmail { get; init; }

    /// <summary>The address as the visitor typed it: the account is created with it, and the mail goes to it.</summary>
    public required string Email { get; set; }

    public required string CompanyName { get; set; }

    public required DateTimeOffset RequestedAt { get; set; }

    /// <summary>SHA-256 of the current link token; <see langword="null"/> until the first mail has been sent.</summary>
    public byte[]? TokenHash { get; set; }

    public DateTimeOffset? TokenExpiresAt { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }
}
