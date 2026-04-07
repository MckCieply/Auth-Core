using Auth.Server.Email;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// The ways an instance can have sign-up off (spec 0009, criteria 1, 2 and 17), as a name that a theory can carry and a way to make the
/// host of each. "no-key" is an instance of <c>v0.1.0</c>: its manifest and its settings.
/// </summary>
public static class SignupVariants
{
    public const string NoKey = "no-key";
    public const string NoKeyWithAddress = "no-key-with-address";
    public const string InviteOnlyWithAddress = "invite-only-with-address";
    public const string SelfServiceWithoutAddress = "self-service-without-address";

    public static TheoryData<string> Off => new() { NoKey, NoKeyWithAddress, InviteOnlyWithAddress, SelfServiceWithoutAddress };

    /// <summary>Call it before the host is built.</summary>
    public static AuthAppFactory Apply(AuthAppFactory factory, string variant)
    {
        ArgumentNullException.ThrowIfNull(factory);

        switch (variant)
        {
            case NoKey:
                return factory;
            case NoKeyWithAddress:
                return factory.WithSetting(MailSettingsLoader.CompleteSignupUrlKey, AuthAppFactory.DefaultCompleteSignupUrl);
            case InviteOnlyWithAddress:
                return factory
                    .WithManifest(AuthAppFactory.DefaultManifest + "\nsignup: invite-only\n")
                    .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, AuthAppFactory.DefaultCompleteSignupUrl);
            case SelfServiceWithoutAddress:
                return factory.WithManifest(AuthAppFactory.SelfServiceManifest);
            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown variant.");
        }
    }
}
