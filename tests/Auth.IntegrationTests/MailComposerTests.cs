using Auth.Infrastructure.Persistence;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

public sealed class MailComposerTests
{
    private const string Token = "abcDEF123_-abcDEF123_-abcDEF123_-abcDEF123_";

    private static MailComposer Composer(string locale = "en", string appName = "speech-to-mail", string reset = "https://app.example.com/reset") =>
        new(new MailSettings
        {
            AppName = appName,
            Locale = locale,
            ResetPasswordUrl = new Uri(reset),
            VerifyEmailUrl = new Uri("https://app.example.com/verify"),
            AcceptInviteUrl = new Uri("https://app.example.com/invite"),
            FromAddress = "no-reply@example.com",
            Smtp = new SmtpSettings { Host = "smtp.invalid", Port = 587, Security = SmtpSecurity.StartTls },
        });

    [Fact]
    public void Reset_mail_in_english()   // criterion 15
    {
        var mail = Composer().Compose(MailKind.PasswordReset, "user@example.com", Token);

        Assert.Equal("user@example.com", mail.To);
        Assert.Equal("Reset your speech-to-mail password", mail.Subject);
        Assert.Contains($"https://app.example.com/reset?token={Token}", mail.TextBody);
        Assert.Contains("speech-to-mail", mail.TextBody);
        Assert.Contains("valid for 1 hour", mail.TextBody);
        Assert.Contains("ignore this message", mail.TextBody);
        Assert.Contains($"href=\"https://app.example.com/reset?token={Token}\"", mail.HtmlBody);
        Assert.Contains("valid for 1 hour", mail.HtmlBody);
        Assert.Contains("<html lang=\"en\">", mail.HtmlBody);
    }

    [Fact]
    public void Verification_mail_in_polish()   // criterion 15
    {
        var mail = Composer("pl").Compose(MailKind.EmailVerification, "user@example.com", Token);

        Assert.Equal("Potwierdź adres e-mail w speech-to-mail", mail.Subject);
        Assert.Contains($"https://app.example.com/verify?token={Token}", mail.TextBody);
        Assert.Contains("ważny przez 24 godziny", mail.TextBody);
        Assert.Contains("zignoruj tę wiadomość", mail.TextBody);
        Assert.Contains("Potwierdź adres e-mail", mail.HtmlBody);      // letters stay letters, not entities
        Assert.Contains("ważny przez 24 godziny", mail.HtmlBody);
        Assert.Contains("<html lang=\"pl\">", mail.HtmlBody);
    }

    [Theory]
    [InlineData("en", MailKind.PasswordReset)]
    [InlineData("en", MailKind.EmailVerification)]
    [InlineData("pl", MailKind.PasswordReset)]
    [InlineData("pl", MailKind.EmailVerification)]
    public void Every_language_and_kind_has_every_text(string locale, MailKind kind)
    {
        var text = MailTexts.For(locale, kind);

        Assert.All(
            new[] { text.Subject, text.Greeting, text.Intro, text.Button, text.Validity, text.LinkHint, text.Ignore },
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Contains("{0}", text.Subject);
        Assert.Contains("{0}", text.Intro);

        var mail = Composer(locale).Compose(kind, "user@example.com", Token);
        Assert.DoesNotContain("{0}", mail.Subject + mail.TextBody + mail.HtmlBody);
    }

    [Fact]
    public void Application_name_is_encoded_in_the_html_part_only()   // Review Focus 3
    {
        var mail = Composer(appName: "Tom & <Jerry>").Compose(MailKind.PasswordReset, "user@example.com", Token);

        Assert.Contains("Tom & <Jerry>", mail.TextBody);
        Assert.Contains("Tom &amp; &lt;Jerry&gt;", mail.HtmlBody);
        Assert.DoesNotContain("<Jerry>", mail.HtmlBody);
        Assert.Equal("Reset your Tom & <Jerry> password", mail.Subject);
    }

    [Fact]
    public void Link_keeps_a_query_string_the_frontend_url_already_has()   // Review Focus 3
    {
        var mail = Composer(reset: "https://app.example.com/account?view=reset")
            .Compose(MailKind.PasswordReset, "user@example.com", Token);

        Assert.Contains($"https://app.example.com/account?view=reset&token={Token}", mail.TextBody);
        Assert.Contains($"https://app.example.com/account?view=reset&amp;token={Token}", mail.HtmlBody);
    }

    [Fact]
    public void Composed_mail_does_not_print_its_content() =>
        Assert.DoesNotContain(Token, Composer().Compose(MailKind.PasswordReset, "user@example.com", Token).ToString());

    public static TheoryData<string, MailKind, string?> Notices => new()
    {
        { "en", MailKind.ProviderLinked, "GitHub" },
        { "en", MailKind.ProviderUnlinked, "Google" },
        { "en", MailKind.PasswordChanged, null },
        { "pl", MailKind.ProviderLinked, "Microsoft" },
        { "pl", MailKind.ProviderUnlinked, "GitHub" },
        { "pl", MailKind.PasswordChanged, null },
    };

    [Theory]
    [MemberData(nameof(Notices))]
    public void A_notice_names_the_provider_and_the_address_in_both_parts_and_holds_no_link(string locale, MailKind kind, string? provider)   // spec 0012, criterion 22
    {
        var mail = Composer(locale).ComposeNotice(kind, "user@example.com", provider);

        Assert.Equal("user@example.com", mail.To);
        Assert.Contains("speech-to-mail", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"" + locale + "\">", mail.HtmlBody, StringComparison.Ordinal);
        foreach (var body in new[] { mail.TextBody, mail.HtmlBody })
        {
            Assert.Contains("user@example.com", body, StringComparison.Ordinal);
            if (provider is not null)
            {
                Assert.Contains(provider, body, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("http", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("app.example.com", body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("en", MailKind.ProviderLinked)]
    [InlineData("en", MailKind.ProviderUnlinked)]
    [InlineData("en", MailKind.PasswordChanged)]
    [InlineData("pl", MailKind.ProviderLinked)]
    [InlineData("pl", MailKind.ProviderUnlinked)]
    [InlineData("pl", MailKind.PasswordChanged)]
    public void Every_language_and_notice_has_every_text(string locale, MailKind kind)   // spec 0012 → Mails: Polish and English
    {
        var text = MailTexts.Notice(locale, kind);

        Assert.All(
            [text.Subject, text.Greeting, text.Intro, text.Advice, text.NoLink],
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Contains("{0}", text.Subject, StringComparison.Ordinal);
        Assert.Contains("{2}", text.Intro, StringComparison.Ordinal);
    }

    [Fact]
    public void A_notice_encodes_the_address_and_the_application_in_the_html_part_only()   // spec 0012 → Mails
    {
        var mail = Composer(appName: "A&B <Notes>").ComposeNotice(MailKind.ProviderLinked, "a&b@example.com", "GitHub");

        Assert.Contains("a&amp;b@example.com", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("A&amp;B &lt;Notes&gt;", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("a&b@example.com", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", mail.Subject, StringComparison.Ordinal);   // the address stays out of the header
    }

    [Fact]
    public void A_provider_notice_without_a_provider_and_another_kind_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Composer().ComposeNotice(MailKind.ProviderLinked, "user@example.com", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => Composer().ComposeNotice(MailKind.PasswordReset, "user@example.com", null));
    }
}
