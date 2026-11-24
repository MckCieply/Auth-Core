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
/// Builds the mails of specs 0004, 0005, 0009, 0010 and 0012 in the configured language, each as plain text and as HTML with the same
/// content. Only configuration, the token and the names below go into a mail: an invitation names its company and role, and an
/// organization sign-up the company its visitor typed (a private sign-up names none). They are encoded in the HTML part and are never in
/// a header; nothing else a requester submitted goes in.
/// </summary>
public sealed class MailComposer(MailSettings settings)
{
    // Every letter stays a letter (Polish diacritics included); only markup characters are escaped.
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    private const string BodyStyle = "font-family: Arial, Helvetica, sans-serif; font-size: 16px; line-height: 1.5; color: #1f2933;";

    /// <param name="companyName">
    /// The company of an invitation or of an organization sign-up; required for <see cref="MailKind.Invitation"/> and for
    /// <see cref="MailKind.Signup"/> unless <paramref name="privateAccount"/> is set, ignored otherwise (a private sign-up names no company).
    /// </param>
    /// <param name="privateAccount">
    /// Spec 0010. For <see cref="MailKind.Signup"/> only: the private variant, which names no company. Ignored otherwise (since spec 0017 an
    /// invitation no longer has a private variant: see <paramref name="accountExists"/>).
    /// </param>
    /// <param name="accountExists">
    /// Spec 0017. For <see cref="MailKind.Invitation"/> only: the invited address has an account (of any kind), and the mail says that
    /// accepting adds the company to it and keeps what it has. Ignored otherwise.
    /// </param>
    public ComposedMail Compose(
        MailKind kind, string recipient, string token, string? companyName = null, bool privateAccount = false, bool accountExists = false)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(token);
        if (kind == MailKind.Invitation && companyName is null)
        {
            throw new ArgumentException("An invitation names its company.", nameof(companyName));
        }

        if (kind == MailKind.Signup && companyName is null && !privateAccount)
        {
            throw new ArgumentException("An organization sign-up names its company.", nameof(companyName));
        }

        var text = (kind, privateAccount, accountExists) switch
        {
            (MailKind.Signup, true, _) => MailTexts.PrivateSignup(settings.Locale),
            (MailKind.Invitation, _, true) => MailTexts.InvitationToExistingAccount(settings.Locale),
            _ => MailTexts.For(settings.Locale, kind),
        };
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
        // The subject takes the application name only: the company is typed by people, and a header is not the place
        // for that. It appears in the body, encoded in its HTML part.
        var subject = string.Format(CultureInfo.InvariantCulture, text.Subject, settings.AppName);
        var intro = string.Format(CultureInfo.InvariantCulture, text.Intro, settings.AppName, companyName);
        // Formatted first and encoded whole, like the intro: the company in it is encoded in the HTML part.
        var paragraph = text.Paragraph is null
            ? null
            : string.Format(CultureInfo.InvariantCulture, text.Paragraph, settings.AppName, companyName);
        var paragraphHtml = paragraph is null ? "" : "\n<p>" + Html.Encode(paragraph) + "</p>";
        var lines = new List<string> { text.Greeting, "", intro, "" };
        if (paragraph is not null)
        {
            lines.Add(paragraph);
            lines.Add("");
        }

        lines.AddRange([link, "", text.Validity, text.Ignore, ""]);

        return new ComposedMail
        {
            To = recipient,
            Subject = subject,
            TextBody = string.Join("\r\n", lines),
            HtmlBody = $"""
                <!DOCTYPE html>
                <html lang="{settings.Locale}">
                <head><meta charset="utf-8"><title>{Html.Encode(subject)}</title></head>
                <body style="{BodyStyle}">
                <p>{Html.Encode(text.Greeting)}</p>
                <p>{Html.Encode(intro)}</p>{paragraphHtml}
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
    /// The mail an address gets when someone signs up with it and it has an account of either kind (spec 0010 → Mails): it says that
    /// someone, perhaps the reader, tried, that the account exists, and how to sign in or reset the password. It holds no link and no token,
    /// and nothing the requester typed. It ends with how to get a new account on purpose (spec 0010, Decision 6).
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
            TextBody = string.Join("\r\n", text.Greeting, "", intro, "", hint, "", text.Deliberate, "", text.Ignore, ""),
            HtmlBody = $"""
                <!DOCTYPE html>
                <html lang="{settings.Locale}">
                <head><meta charset="utf-8"><title>{Html.Encode(subject)}</title></head>
                <body style="{BodyStyle}">
                <p>{Html.Encode(text.Greeting)}</p>
                <p>{Html.Encode(intro)}</p>
                <p>{Html.Encode(hint)}</p>
                <p>{Html.Encode(text.Deliberate)}</p>
                <p style="font-size: 14px; color: #52606d;">{Html.Encode(text.Ignore)}</p>
                </body>
                </html>
                """,
        };
    }

    /// <summary>
    /// A notice of spec 0012 → Mails: what changed on the account, and what to do if it was not the reader. No link and no token: it names the
    /// product's sign-in screen in words only. The address and the provider's display name are in the body, encoded in its HTML part; the subject
    /// names the application and the provider only.
    /// </summary>
    public ComposedMail ComposeNotice(MailKind kind, string recipient, string? providerDisplayName)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        if (kind is not (MailKind.ProviderLinked or MailKind.ProviderUnlinked or MailKind.PasswordChanged))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a notice.");
        }

        if (kind != MailKind.PasswordChanged && providerDisplayName is null)
        {
            throw new ArgumentException("A provider notice names its provider.", nameof(providerDisplayName));
        }

        var text = MailTexts.Notice(settings.Locale, kind);
        string Format(string template) => string.Format(CultureInfo.InvariantCulture, template, settings.AppName, providerDisplayName, recipient);
        var subject = Format(text.Subject);
        var intro = Format(text.Intro);
        var advice = Format(text.Advice);
        var noLink = Format(text.NoLink);

        return new ComposedMail
        {
            To = recipient,
            Subject = subject,
            TextBody = string.Join("\r\n", text.Greeting, "", intro, "", advice, "", noLink, ""),
            HtmlBody = $"""
                <!DOCTYPE html>
                <html lang="{settings.Locale}">
                <head><meta charset="utf-8"><title>{Html.Encode(subject)}</title></head>
                <body style="{BodyStyle}">
                <p>{Html.Encode(text.Greeting)}</p>
                <p>{Html.Encode(intro)}</p>
                <p>{Html.Encode(advice)}</p>
                <p style="font-size: 14px; color: #52606d;">{Html.Encode(noLink)}</p>
                </body>
                </html>
                """,
        };
    }
}
