namespace Auth.Infrastructure.Persistence;

/// <summary>The kinds of mail the service sends. The values are stored: never renumber them.</summary>
public enum MailKind : short
{
    PasswordReset = 1,
    EmailVerification = 2,
    Invitation = 3,
    Signup = 4,

    // Spec 0012 → Mails: the notices of a change to how the account signs in. No link, no token.
    ProviderLinked = 5,
    ProviderUnlinked = 6,
    PasswordChanged = 7,

    /// <summary>Never the kind of a queued mail: the key under which the three notices share their limit in <c>MailRequestLimits</c> (Amendment A10).</summary>
    NoticeLimit = 100,
}
