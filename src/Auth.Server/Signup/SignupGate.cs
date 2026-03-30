using Auth.Server.Email;
using Auth.Server.Tenancy;

namespace Auth.Server.Signup;

/// <summary>
/// Whether sign-up is on (spec 0009 → General rules): the manifest says <c>signup: self-service</c> <b>and</b> the completion address is
/// configured. It reads the manifest of the process at the moment of the call, as every request does. A manifest that asks for sign-up
/// without the address leaves it off, and that is a misconfiguration the start warns about and the health check reports.
/// </summary>
public sealed partial class SignupGate(ManifestHolder manifest, MailSettings mail, ILogger<SignupGate> logger)
{
    /// <summary>The manifest says <c>self-service</c>, whether or not the address is there.</summary>
    private bool Wanted => manifest.Current.Signup == SignupMode.SelfService;

    public bool IsOn => Wanted && mail.CompleteSignupUrl is not null;

    /// <summary>The manifest asks for sign-up but the completion address is not set.</summary>
    public bool MissingAddress => Wanted && mail.CompleteSignupUrl is null;

    /// <summary>What <c>GET /auth/options</c> says: <c>self-service</c> only while sign-up is on.</summary>
    public string ModeName => IsOn ? SignupModes.SelfServiceName : SignupModes.InviteOnlyName;

    /// <summary>Called once at start, after the manifest is active.</summary>
    public void WarnIfMissingAddress()
    {
        if (MissingAddress)
        {
            LogMissingAddress(logger, MailSettingsLoader.CompleteSignupUrlKey);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The manifest says signup: self-service, but {Key} is not set: sign-up is off.")]
    private static partial void LogMissingAddress(ILogger logger, string key);
}
