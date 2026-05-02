namespace Auth.Infrastructure.Persistence;

/// <summary>What a provider round trip was for (spec 0011 → Provider tickets), stored as a <c>smallint</c>.</summary>
public enum ProviderTicketPurpose : short
{
    /// <summary>Sign-in, linking and sign-up: the exchange decides which.</summary>
    SignIn = 1,

    /// <summary>The acceptance of an invitation.</summary>
    Invite = 2,

    /// <summary>The confirmation of a self-deletion.</summary>
    Reauth = 3,
}
