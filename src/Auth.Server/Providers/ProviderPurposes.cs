using Auth.Infrastructure.Persistence;

namespace Auth.Server.Providers;

/// <summary>The purposes of a round trip as the fragment names them (spec 0011 → Settled 1): <c>sign_in</c>, <c>invite</c>, <c>reauth</c>.</summary>
public static class ProviderPurposes
{
    public const string SignInName = "sign_in";
    public const string InviteName = "invite";
    public const string ReauthName = "reauth";

    public static string NameOf(ProviderTicketPurpose purpose) => purpose switch
    {
        ProviderTicketPurpose.SignIn => SignInName,
        ProviderTicketPurpose.Invite => InviteName,
        ProviderTicketPurpose.Reauth => ReauthName,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown purpose."),
    };

    /// <summary>Exactly one of the three names.</summary>
    public static bool TryParse(string? name, out ProviderTicketPurpose purpose)
    {
        (var known, purpose) = name switch
        {
            SignInName => (true, ProviderTicketPurpose.SignIn),
            InviteName => (true, ProviderTicketPurpose.Invite),
            ReauthName => (true, ProviderTicketPurpose.Reauth),
            _ => (false, ProviderTicketPurpose.SignIn),
        };
        return known;
    }
}

/// <summary>
/// What Auth-Core puts into the state of a round trip, beside OpenIddict's own: it travels encrypted (the client's credentials are the instance's
/// key files) and comes back at the callback whenever the state could be read. Never the invitation token, only its hash.
/// </summary>
public static class ProviderStateItems
{
    public const string Purpose = "purpose";
    public const string Provider = "provider";

    /// <summary>The SHA-256 of the invitation's link token, in hexadecimal.</summary>
    public const string InviteHash = "invite_hash";
}
