using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Tenancy;

namespace Auth.Server.Signup;

/// <summary>
/// Whether sign-up is on (spec 0009 → General rules; spec 0010: two independent switches). Organization sign-up is on when the manifest says
/// <c>signup: self-service</c> <b>and</b> the completion address is configured; private sign-up when it says <c>private.signup: self-service</c>
/// and the same address is configured. It reads the manifest of the process at the moment of the call, as every request does. A switch wanted
/// without the address stays off, and that is a misconfiguration the start warns about and the health check reports.
/// </summary>
public sealed partial class SignupGate(ManifestHolder manifest, MailSettings mail, ILogger<SignupGate> logger)
{
    private bool OrganizationWanted => manifest.Current.Signup == SignupMode.SelfService;

    private bool PrivateWanted => manifest.Current.PrivateSignup;

    private bool HasAddress => mail.CompleteSignupUrl is not null;

    /// <summary>Organization sign-up (spec 0009) is on.</summary>
    public bool IsOn => OrganizationWanted && HasAddress;

    /// <summary>Private sign-up (spec 0010) is on.</summary>
    public bool PrivateOn => PrivateWanted && HasAddress;

    /// <summary>At least one of the two is on: otherwise the three sign-up endpoints answer <c>403</c> before they read the body.</summary>
    public bool AnyOn => IsOn || PrivateOn;

    /// <summary>The switch of one kind of sign-up.</summary>
    public bool IsOnFor(SignupKind kind) => kind == SignupKind.Private ? PrivateOn : IsOn;

    /// <summary>The manifest asks for either kind of sign-up but the completion address is not set.</summary>
    public bool MissingAddress => (OrganizationWanted || PrivateWanted) && !HasAddress;

    /// <summary>What <c>GET /auth/options</c> says in <c>signup</c>: <c>self-service</c> only while organization sign-up is on.</summary>
    public string ModeName => IsOn ? SignupModes.SelfServiceName : SignupModes.InviteOnlyName;

    /// <summary>What <c>GET /auth/options</c> says in <c>private_signup</c>: <c>self-service</c> only while private sign-up is on.</summary>
    public string PrivateModeName => PrivateOn ? SignupModes.SelfServiceName : SignupModes.OffName;

    /// <summary>Called once at start, after the manifest is active: one warning for each switch that is wanted without the address.</summary>
    public void WarnIfMissingAddress()
    {
        if (HasAddress)
        {
            return;
        }

        if (OrganizationWanted)
        {
            LogMissingAddress(logger, MailSettingsLoader.CompleteSignupUrlKey);
        }

        if (PrivateWanted)
        {
            LogMissingAddressForPrivate(logger, MailSettingsLoader.CompleteSignupUrlKey);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The manifest says signup: self-service, but {Key} is not set: sign-up is off.")]
    private static partial void LogMissingAddress(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The manifest says private.signup: self-service, but {Key} is not set: private sign-up is off.")]
    private static partial void LogMissingAddressForPrivate(ILogger logger, string key);
}
