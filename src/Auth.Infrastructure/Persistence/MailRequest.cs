namespace Auth.Infrastructure.Persistence;

/// <summary>
/// One request for a mail, waiting to be handled (spec 0004, Decisions 5 and 12). It names the address as it was
/// submitted, normalised, and nothing about an account: whether there is one is found out when the row is handled.
/// </summary>
public sealed class MailRequest
{
    public long Id { get; init; }

    public required MailKind Kind { get; init; }

    public required string NormalizedEmail { get; init; }

    /// <summary>
    /// The invitation a <see cref="MailKind.Invitation"/> request is for; <see langword="null"/> for the other kinds.
    /// Deliberately not a foreign key: cancelling an invitation must not wait for a mail that is being sent, so a
    /// request whose invitation is gone is dropped by the dispatcher instead.
    /// </summary>
    public Guid? InviteId { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>Failed sends so far.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }
}
