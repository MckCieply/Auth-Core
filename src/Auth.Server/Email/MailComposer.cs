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
/// Builds the two mails of spec 0004 in the configured language, each as plain text and as HTML with the same
/// content. Only configuration and the token go into a mail; nothing a requester submitted does.
/// </summary>
public sealed class MailComposer(MailSettings settings)
{
    // Every letter stays a letter (Polish diacritics included); only markup characters are escaped.
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    public ComposedMail Compose(MailKind kind, string recipient, string token)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(token);

        var text = MailTexts.For(settings.Locale, kind);
        var target = kind == MailKind.PasswordReset ? settings.ResetPasswordUrl : settings.VerifyEmailUrl;
        var link = QueryHelpers.AddQueryString(target.AbsoluteUri, "token", token);
        var subject = string.Format(CultureInfo.InvariantCulture, text.Subject, settings.AppName);
        var intro = string.Format(CultureInfo.InvariantCulture, text.Intro, settings.AppName);

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
                <body style="font-family: Arial, Helvetica, sans-serif; font-size: 16px; line-height: 1.5; color: #1f2933;">
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
}
