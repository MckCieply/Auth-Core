namespace Auth.Infrastructure.Persistence;

/// <summary>
/// The mail limit of one address and one kind of mail (spec 0004): the accepted requests of the current window.
/// The key is a hash, never the address, and a row exists for addresses without an account too.
/// </summary>
public sealed class MailRequestLimit
{
    /// <summary>SHA-256 of the normalised email: 32 bytes whatever was submitted.</summary>
    public required byte[] IdentifierHash { get; init; }

    public required MailKind Kind { get; init; }

    public DateTimeOffset WindowStartedAt { get; set; }

    /// <summary>Accepted requests in the window; 0 only on a row that has just been created.</summary>
    public int WindowCount { get; set; }

    public DateTimeOffset LastAcceptedAt { get; set; }
}
