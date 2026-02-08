using Microsoft.AspNetCore.WebUtilities;
using MimeKit;

namespace Auth.Server.Email;

public enum SmtpSecurity
{
    /// <summary>Plain SMTP. Development only: a local mail catcher.</summary>
    None,

    /// <summary>Plain connection upgraded with STARTTLS (usually port 587).</summary>
    StartTls,

    /// <summary>TLS from the first byte (usually port 465).</summary>
    Tls,
}

/// <summary>Where mail is handed over. A class, not a record: a record would print the password.</summary>
public sealed class SmtpSettings
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public required SmtpSecurity Security { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }
}

/// <summary>
/// What a mail needs to know about the consumer: its name and language, where its reset and verification screens
/// are, who the mail is from and how to send it. The <c>Auth:App</c> keys mirror <c>app.*</c> of the manifest in
/// design.md, so the manifest loader will fill the same settings.
/// </summary>
public sealed class MailSettings
{
    public required string AppName { get; init; }

    /// <summary><c>pl</c> or <c>en</c>.</summary>
    public required string Locale { get; init; }

    public required Uri ResetPasswordUrl { get; init; }

    public required Uri VerifyEmailUrl { get; init; }

    /// <summary>The product's invitation screen (spec 0005): the link of an invitation mail is this URL plus <c>token</c>.</summary>
    public required Uri AcceptInviteUrl { get; init; }

    public required string FromAddress { get; init; }

    public required SmtpSettings Smtp { get; init; }
}

public static class MailSettingsLoader
{
    public const string AppNameKey = "Auth:App:Name";
    public const string LocaleKey = "Auth:App:Locale";
    public const string ResetPasswordUrlKey = "Auth:App:FrontendUrls:ResetPassword";
    public const string VerifyEmailUrlKey = "Auth:App:FrontendUrls:VerifyEmail";
    public const string AcceptInviteUrlKey = "Auth:App:FrontendUrls:AcceptInvite";
    public const string FromKey = "Auth:Email:From";
    public const string SmtpHostKey = "Auth:Email:Smtp:Host";
    public const string SmtpPortKey = "Auth:Email:Smtp:Port";
    public const string SmtpSecurityKey = "Auth:Email:Smtp:Security";
    public const string SmtpUsernameKey = "Auth:Email:Smtp:Username";
    public const string SmtpPasswordKey = "Auth:Email:Smtp:Password";

    private const int MaxAppNameLength = 100;

    /// <summary>Reads and checks the mail settings. Errors name the key and never echo a value.</summary>
    /// <param name="isDevelopment">Development accepts <c>http</c> links and a mail server without TLS.</param>
    /// <exception cref="InvalidOperationException">A value is missing or invalid.</exception>
    public static MailSettings Load(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var appName = Required(configuration, AppNameKey);
        if (appName.Length > MaxAppNameLength || appName.Any(char.IsControl))
        {
            throw Invalid(AppNameKey, $"must be at most {MaxAppNameLength} characters without control characters");
        }

        var locale = Required(configuration, LocaleKey);
        if (locale is not ("pl" or "en"))
        {
            throw Invalid(LocaleKey, "must be 'pl' or 'en'");
        }

        var from = Required(configuration, FromKey);
        var at = from.LastIndexOf('@');
        if (!MailboxAddress.TryParse(from, out var mailbox)
            || !string.Equals(mailbox.Address, from, StringComparison.Ordinal)
            || at <= 0 || at == from.Length - 1)
        {
            throw Invalid(FromKey, "must be a single email address");
        }

        var host = Required(configuration, SmtpHostKey);
        if (!int.TryParse(Required(configuration, SmtpPortKey), out var port) || port is < 1 or > 65535)
        {
            throw Invalid(SmtpPortKey, "must be a port number between 1 and 65535");
        }

        SmtpSecurity security = Required(configuration, SmtpSecurityKey) switch
        {
            "none" => SmtpSecurity.None,
            "starttls" => SmtpSecurity.StartTls,
            "tls" => SmtpSecurity.Tls,
            _ => throw Invalid(SmtpSecurityKey, "must be 'none', 'starttls' or 'tls'"),
        };
        if (security == SmtpSecurity.None && !isDevelopment)
        {
            throw Invalid(SmtpSecurityKey, "must use TLS outside the Development environment");
        }

        var username = configuration[SmtpUsernameKey];
        var password = configuration[SmtpPasswordKey];
        if (string.IsNullOrEmpty(username) != string.IsNullOrEmpty(password))
        {
            var missing = string.IsNullOrEmpty(username) ? SmtpUsernameKey : SmtpPasswordKey;
            throw Invalid(missing, "must be set when the other SMTP credential is");
        }

        return new MailSettings
        {
            AppName = appName,
            Locale = locale,
            ResetPasswordUrl = FrontendUrl(configuration, ResetPasswordUrlKey, isDevelopment),
            VerifyEmailUrl = FrontendUrl(configuration, VerifyEmailUrlKey, isDevelopment),
            AcceptInviteUrl = FrontendUrl(configuration, AcceptInviteUrlKey, isDevelopment),
            FromAddress = from,
            Smtp = new SmtpSettings
            {
                Host = host,
                Port = port,
                Security = security,
                Username = string.IsNullOrEmpty(username) ? null : username,
                Password = string.IsNullOrEmpty(password) ? null : password,
            },
        };
    }

    private static Uri FrontendUrl(IConfiguration configuration, string key, bool isDevelopment)
    {
        if (!Uri.TryCreate(Required(configuration, key), UriKind.Absolute, out var url)
            || !(url.Scheme == Uri.UriSchemeHttps || (isDevelopment && url.Scheme == Uri.UriSchemeHttp))
            || url.Fragment.Length > 0
            || url.UserInfo.Length > 0
            || QueryHelpers.ParseQuery(url.Query).ContainsKey("token"))
        {
            // A fragment would swallow the token; the link adds a 'token' parameter of its own.
            throw Invalid(key, isDevelopment
                ? "must be an absolute http or https URL without credentials, a fragment or a 'token' parameter"
                : "must be an absolute https URL without credentials, a fragment or a 'token' parameter");
        }

        return url;
    }

    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            // Name the key only; never echo a value.
            throw new InvalidOperationException($"Configuration value '{key}' is missing or blank.");
        }

        return value;
    }

    private static InvalidOperationException Invalid(string key, string rule) =>
        new($"Configuration value '{key}' {rule}.");
}
