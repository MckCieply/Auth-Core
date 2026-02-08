namespace Auth.Infrastructure.Persistence;

/// <summary>The kinds of mail the service sends. The values are stored: never renumber them.</summary>
public enum MailKind : short
{
    PasswordReset = 1,
    EmailVerification = 2,
    Invitation = 3,
}
