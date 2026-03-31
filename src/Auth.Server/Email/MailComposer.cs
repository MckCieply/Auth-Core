using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Auth.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;

namespace Auth.Server.Email;

/// <summary>A mail ready to send. A class, not a record: nothing should print a body, which holds the link token.</summary>
public sealed class ComposedMail
{
    public required string To { get; init; }

    public required string Subject { get; init; }

    public required string TextBody { get; init; }

    public required string HtmlBody { get; init; }
}

/// <summary>
/// Builds the mails of specs 0004, 0005 and 0009 in the configured language, each as plain text and as HTML with the same content. Only
/// configuration, the token and the names below go into a mail: an invitation names its company and role, and a sign-up the company its
/// visitor typed. They are encoded in the HTML part and are never in a header; nothing else a requester submitted goes in.
/// </summary>
public sealed class MailComposer(MailSettings settings)
{
    // Every letter stays a letter (Polish diacritics included); only markup characters are escaped.
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    private const string BodyStyle = "font-family: Arial, Helvetica, sans-serif; font-size: 16px; line-height: 1.5; color: #1f2933;";

    /// <param name="companyName">
    /// The company of an invitation or of a sign-up; required for <see cref="MailKind.Invitation"/> and <see cref="MailKind.Signup"/>,
    /// ignored otherwise.
    /// </param>
    /// <param name="roleName">The role of an invitation; required for <see cref="MailKind.Invitation"/>, ignored otherwise.</param>
    public ComposedMail Compose(MailKind kind, string recipient, string token, string? companyName = null, string? roleName = null)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(token);
        if (kind == MailKind.Invitation && (companyName is null || roleName is null))
        {
            throw new ArgumentException("An invitation names its company and its role.", nameof(kind));
        }

        if (kind == MailKind.Signup && companyName is null)
        {
            throw new ArgumentException("A sign-up names its company.", nameof(companyName));
        }

        var text = MailTexts.For(settings.Locale, kind);
        var target = kind switch
        {
            MailKind.PasswordReset => settings.ResetPasswordUrl,
            MailKind.EmailVerification => settings.VerifyEmailUrl,
            MailKind.Invitation => settings.AcceptInviteUrl,
            MailKind.Signup => settings.CompleteSignupUrl
                ?? throw new InvalidOperationException("The completion address of sign-up is not configured."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of mail."),
        };
        var link = QueryHelpers.AddQueryString(target.AbsoluteUri, "token", token);
        // The subject takes the application name only: the company and the role are typed by people, and a header is not the place
        // for that. They appear in the body, encoded in its HTML part.
        var subject = string.Format(CultureInfo.InvariantCulture, text.Subject, settings.AppName);
        var intro = string.Format(CultureInfo.InvariantCulture, text.Intro, settings.AppName, companyName, roleName);

        return new ComposedMail
        {
            To = recipient,
            Subject = subject,
            TextBody = string.Join(
                "\r\n",
                text.Greeting, "", intro, "", link, "", text.Validity, text.Ignore, ""),
            HtmlBody = $"""
                <!DOCTYPE html>
                <html lang="{settings.Locale}">
                <head><meta charset="utf-8"><title>{Html.Encode(subject)}</title></head>
                <body style="{BodyStyle}">
                <p>{Html.Encode(text.Greeting)}</p>
                <p>{Html.Encode(intro)}</p>
                <p><a href="{Html.Encode(link)}" style="display: inline-block; padding: 12px 20px; background-color: #1f6feb; color: #ffffff; text-decoration: none; border-radius: 6px;">{Html.Encode(text.Button)}</a></p>
                <p>{Html.Encode(text.LinkHint)}<br><a href="{Html.Encode(link)}">{Html.Encode(link)}</a></p>
                <p>{Html.Encode(text.Validity)}</p>
                <p style="font-size: 14px; color: #52606d;">{Html.Encode(text.Ignore)}</p>
                </body>
                </html>
                """,
        };
    }

    /// <summary>
    /// The mail an address with an account gets when someone signs up with it (spec 0009 → Mails): it says that someone, perhaps the
    /// reader, tried, that the account exists, and how to sign in or reset the password. It holds no link and no token, and nothing
    /// the requester typed.
    /// </summary>
    public ComposedMail ComposeAccountExists(string recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        var text = MailTexts.AccountExists(settings.Locale);
        var subject = string.Format(CultureInfo.InvariantCulture, text.Subject, settings.AppName);
        var intro = string.Format(CultureInfo.InvariantCulture, text.Intro, settings.AppName);
        var hint = string.Format(CultureInfo.InvariantCulture, text.SignInHint, settings.AppName);

        return new ComposedMail
        {
            To = recipient,
            Subject = subject,
            TextBody = string.Join("\r\n", text.Greeting, "", intro, "", hint, "", text.Ignore, ""),
            HtmlBody = $"""
                <!DOCTYPE html>
                <html lang="{settings.Locale}">
                <head><meta charset="utf-8"><title>{Html.Encode(subject)}</title></head>
                <body style="{BodyStyle}">
                <p>{Html.Encode(text.Greeting)}</p>
                <p>{Html.Encode(intro)}</p>
                <p>{Html.Encode(hint)}</p>
                <p style="font-size: 14px; color: #52606d;">{Html.Encode(text.Ignore)}</p>
                </body>
                </html>
                """,
        };
    }
}
