namespace Auth.Infrastructure.Persistence;

/// <summary>
/// A provider ticket (spec 0011 → Provider tickets): what a successful callback proved, waiting for the request that uses it. Only hashes are
/// stored: of the ticket, which travels in the fragment, and of the value of the browser's <c>auth_pt</c> cookie, which binds it to that browser.
/// It names no account (no foreign key): the exchange decides which account, if any.
/// </summary>
public sealed class ProviderTicket
{
    public Guid Id { get; init; }

    /// <summary>SHA-256 of the ticket.</summary>
    public required byte[] TicketHash { get; init; }

    /// <summary>SHA-256 of the value of the <c>auth_pt</c> cookie set with the ticket.</summary>
    public required byte[] BindingHash { get; init; }

    public required ProviderTicketPurpose Purpose { get; init; }

    /// <summary><c>google</c> or <c>microsoft</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>The identity key (Google <c>sub</c>, Microsoft <c>{tid}:{oid}</c>): the <c>ProviderKey</c> of a login.</summary>
    public required string IdentityKey { get; init; }

    /// <summary>The address as the provider gave it; <see langword="null"/> when it gave none.</summary>
    public string? Email { get; init; }

    public bool EmailVerified { get; init; }

    /// <summary>For an invitation: SHA-256 of its link token. Never the token.</summary>
    public byte[]? InviteTokenHash { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}
