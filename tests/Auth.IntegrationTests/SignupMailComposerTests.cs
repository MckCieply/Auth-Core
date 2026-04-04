using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

public sealed class SignupMailComposerTests
{
    private const string Token = "abcDEF123_-abcDEF123_-abcDEF123_-abcDEF123_";
    private const string Link = "https://app.example.com/complete-signup?token=" + Token;

    private static MailComposer Composer(string locale = "en", string appName = "speech-to-mail", string? complete = "https://app.example.com/complete-signup") =>
        new(new MailSettings
        {
            AppName = appName,
            Locale = locale,
            ResetPasswordUrl = new Uri("https://app.example.com/reset"),
            VerifyEmailUrl = new Uri("https://app.example.com/verify"),
            AcceptInviteUrl = new Uri("https://app.example.com/invite"),
            CompleteSignupUrl = complete is null ? null : new Uri(complete),
            FromAddress = "no-reply@example.com",
            Smtp = new SmtpSettings { Host = "smtp.invalid", Port = 587, Security = SmtpSecurity.StartTls },
        });

    [Fact]
    public void The_complete_mail_in_english_names_the_application_the_company_the_link_and_the_lifetime()   // criteria 3, 21
    {
        var mail = Composer().Compose(MailKind.Signup, "anna@firma.test", Token, "Firma");

        Assert.Equal("anna@firma.test", mail.To);
        Assert.Equal("Complete your sign-up for speech-to-mail", mail.Subject);
        Assert.Contains("speech-to-mail", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("Firma", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(Link, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("valid for 24 hours", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("ignore this message", mail.TextBody, StringComparison.Ordinal);
        // The link also serves an account that keeps its password (Amendment 3): the text does not promise a password to choose.
        Assert.Contains("use the link below to finish signing up.", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("password", mail.TextBody + mail.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"href=\"{Link}\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Firma", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("valid for 24 hours", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\">", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("{", mail.Subject + mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
        AssertHtmlHoldsTheText(mail);
    }

    [Fact]
    public void The_complete_mail_in_polish_keeps_its_letters()   // criterion 21
    {
        var mail = Composer("pl").Compose(MailKind.Signup, "anna@firma.test", Token, "Żółw i Spółka");

        Assert.Equal("Dokończ rejestrację w speech-to-mail", mail.Subject);
        Assert.Contains("Żółw i Spółka", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("Żółw i Spółka", mail.HtmlBody, StringComparison.Ordinal);   // letters stay letters, not entities
        Assert.Contains(Link, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("ważny przez 24 godziny", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("użyj poniższego linku, aby dokończyć rejestrację.", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("hasł", mail.TextBody + mail.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zignoruj tę wiadomość", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"pl\">", mail.HtmlBody, StringComparison.Ordinal);
        AssertHtmlHoldsTheText(mail);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public void The_html_and_the_text_hold_the_same_link(string locale)   // criterion 21
    {
        var mail = Composer(locale).Compose(MailKind.Signup, "anna@firma.test", Token, "Firma");

        var inText = Regex.Matches(mail.TextBody, @"https://app\.example\.com/complete-signup\?token=[A-Za-z0-9_-]{43}");
        var inHtml = Regex.Matches(mail.HtmlBody, @"https://app\.example\.com/complete-signup\?token=[A-Za-z0-9_-]{43}");
        Assert.Single(inText);
        Assert.NotEmpty(inHtml);
        Assert.All(inHtml, link => Assert.Equal(inText[0].Value, link.Value));
    }

    [Fact]
    public void The_company_name_is_encoded_in_the_html_and_is_not_in_the_subject()   // Review Focus 4: a visitor typed it
    {
        var mail = Composer().Compose(MailKind.Signup, "anna@firma.test", Token, "Tom & <Jerry>");

        Assert.Contains("Tom & <Jerry>", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("Tom &amp; &lt;Jerry&gt;", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<Jerry>", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Jerry", mail.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void A_company_name_with_braces_is_not_a_format_string()
    {
        var mail = Composer().Compose(MailKind.Signup, "anna@firma.test", Token, "{0} {1} {2}");

        Assert.Contains("{0} {1} {2}", mail.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void The_account_exists_mail_in_english_has_no_link_and_no_token()   // criterion 4
    {
        var mail = Composer().ComposeAccountExists("anna@firma.test");

        Assert.Equal("anna@firma.test", mail.To);
        Assert.Equal("You already have an account on speech-to-mail", mail.Subject);
        Assert.Contains("already exists", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("\"forgot password\"", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("ignore this message", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\">", mail.HtmlBody, StringComparison.Ordinal);
        AssertNoLinkAndNoToken(mail);
        AssertHtmlHoldsTheText(mail);
    }

    [Fact]
    public void The_account_exists_mail_in_polish_has_no_link_and_no_token()   // criteria 4, 21
    {
        var mail = Composer("pl").ComposeAccountExists("anna@firma.test");

        Assert.Equal("Masz już konto w speech-to-mail", mail.Subject);
        Assert.Contains("już istnieje", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"pl\">", mail.HtmlBody, StringComparison.Ordinal);
        AssertNoLinkAndNoToken(mail);
        AssertHtmlHoldsTheText(mail);
    }

    [Fact]
    public void The_application_name_is_encoded_in_the_html_of_the_account_exists_mail()
    {
        var mail = Composer(appName: "Tom & <Jerry>").ComposeAccountExists("anna@firma.test");

        Assert.Equal("You already have an account on Tom & <Jerry>", mail.Subject);
        Assert.Contains("Tom &amp; &lt;Jerry&gt;", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<Jerry>", mail.HtmlBody.Replace("<title>You already have an account on Tom &amp; &lt;Jerry&gt;</title>", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        AssertHtmlHoldsTheText(mail);
    }

    // The HTML and the text part say the same: every line of the text is in the HTML, encoded.
    private static void AssertHtmlHoldsTheText(ComposedMail mail)
    {
        var encoder = HtmlEncoder.Create(UnicodeRanges.All);
        Assert.All(
            mail.TextBody.Split("\r\n").Where(line => line.Length > 0),
            line => Assert.Contains(encoder.Encode(line), mail.HtmlBody, StringComparison.Ordinal));
    }

    private static void AssertNoLinkAndNoToken(ComposedMail mail)
    {
        foreach (var part in new[] { mail.Subject, mail.TextBody, mail.HtmlBody })
        {
            Assert.DoesNotContain("://", part, StringComparison.Ordinal);
            Assert.DoesNotContain("token", part, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href", part, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{", part, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public void Every_language_has_every_text_of_both_mails(string locale)   // criterion 21
    {
        var complete = MailTexts.For(locale, MailKind.Signup);
        Assert.All(
            new[] { complete.Subject, complete.Greeting, complete.Intro, complete.Button, complete.Validity, complete.LinkHint, complete.Ignore },
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Contains("{0}", complete.Subject, StringComparison.Ordinal);
        Assert.Contains("{0}", complete.Intro, StringComparison.Ordinal);
        Assert.Contains("{1}", complete.Intro, StringComparison.Ordinal);

        var exists = MailTexts.AccountExists(locale);
        Assert.All(
            new[] { exists.Subject, exists.Greeting, exists.Intro, exists.SignInHint, exists.Ignore },
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Contains("{0}", exists.Subject, StringComparison.Ordinal);
        Assert.Contains("{0}", exists.Intro, StringComparison.Ordinal);
        Assert.Contains("{0}", exists.SignInHint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_mail_needs_the_company_and_the_configured_address()
    {
        Assert.Throws<ArgumentException>(() => Composer().Compose(MailKind.Signup, "anna@firma.test", Token));
        Assert.Throws<InvalidOperationException>(() => Composer(complete: null).Compose(MailKind.Signup, "anna@firma.test", Token, "Firma"));
    }

    [Fact]
    public void An_unknown_kind_of_mail_is_not_composed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Composer().Compose((MailKind)99, "anna@firma.test", Token, "Firma", "admin"));
    }
}
