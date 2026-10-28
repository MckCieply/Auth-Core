using Auth.Infrastructure.Persistence;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

public sealed class InvitationMailComposerTests
{
    private const string Token = "abcDEF123_-abcDEF123_-abcDEF123_-abcDEF123_";

    private static MailComposer Composer(string locale = "en", string appName = "speech-to-mail", string invite = "https://app.example.com/invite") =>
        new(new MailSettings
        {
            AppName = appName,
            Locale = locale,
            ResetPasswordUrl = new Uri("https://app.example.com/reset"),
            VerifyEmailUrl = new Uri("https://app.example.com/verify"),
            AcceptInviteUrl = new Uri(invite),
            FromAddress = "no-reply@example.com",
            Smtp = new SmtpSettings { Host = "smtp.invalid", Port = 587, Security = SmtpSecurity.StartTls },
        });

    [Fact]
    public void Invitation_in_english_names_the_application_the_company_the_link_and_the_lifetime()   // criterion 2
    {
        var mail = Composer().Compose(MailKind.Invitation, "worker@acme.test", Token, "Acme");

        Assert.Equal("worker@acme.test", mail.To);
        Assert.Equal("You are invited to speech-to-mail", mail.Subject);
        Assert.Contains("speech-to-mail", mail.TextBody);
        Assert.Contains("Acme", mail.TextBody);
        Assert.Contains(
            "You have been invited to join Acme on speech-to-mail. Use the link below to choose a password and accept the invitation.",
            mail.TextBody,
            StringComparison.Ordinal);
        Assert.Contains($"https://app.example.com/invite?token={Token}", mail.TextBody);
        Assert.Contains("valid for 7 days", mail.TextBody);
        Assert.Contains("ignore this message", mail.TextBody);
        Assert.Contains($"href=\"https://app.example.com/invite?token={Token}\"", mail.HtmlBody);
        Assert.Contains("valid for 7 days", mail.HtmlBody);
        Assert.Contains("<html lang=\"en\">", mail.HtmlBody);
        Assert.DoesNotContain("{", mail.Subject + mail.TextBody + mail.HtmlBody);
    }

    [Fact]
    public void Invitation_in_polish_keeps_its_letters()   // criterion 2
    {
        var mail = Composer("pl").Compose(MailKind.Invitation, "worker@acme.test", Token, "Żółw i Spółka");

        Assert.Equal("Zaproszenie do speech-to-mail", mail.Subject);
        Assert.Contains("Żółw i Spółka", mail.TextBody);
        Assert.Contains(
            "Zaproszono Cię do firmy Żółw i Spółka w speech-to-mail. Użyj poniższego linku, aby ustawić hasło i przyjąć zaproszenie.",
            mail.TextBody,
            StringComparison.Ordinal);
        Assert.Contains($"https://app.example.com/invite?token={Token}", mail.TextBody);
        Assert.Contains("ważny przez 7 dni", mail.TextBody);
        Assert.Contains("zignoruj tę wiadomość", mail.TextBody);
        Assert.Contains("Żółw i Spółka", mail.HtmlBody);      // letters stay letters, not entities
        Assert.Contains("ważny przez 7 dni", mail.HtmlBody);
        Assert.Contains("<html lang=\"pl\">", mail.HtmlBody);
    }

    [Fact]
    public void The_company_name_is_encoded_in_the_html_part_and_never_in_a_header()   // criterion 23
    {
        var mail = Composer().Compose(MailKind.Invitation, "worker@acme.test", Token, "Tom & <Jerry>");

        Assert.Contains("Tom & <Jerry>", mail.TextBody);
        Assert.Contains("Tom &amp; &lt;Jerry&gt;", mail.HtmlBody);
        Assert.DoesNotContain("<Jerry>", mail.HtmlBody);
        // The subject is a header: the name a member typed is not in it, nor in the recipient.
        Assert.Equal("You are invited to speech-to-mail", mail.Subject);
        Assert.DoesNotContain("Jerry", mail.Subject + mail.To);
    }

    [Fact]
    public void A_name_that_looks_like_a_format_placeholder_is_printed_as_it_is()
    {
        var mail = Composer().Compose(MailKind.Invitation, "worker@acme.test", Token, "{0}{1}{2}");

        Assert.Contains("{0}{1}{2}", mail.TextBody);
        Assert.Contains(
            "You have been invited to join {0}{1}{2} on speech-to-mail. Use the link below to choose a password and accept the invitation.",
            mail.TextBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Link_keeps_a_query_string_the_invitation_screen_already_has()
    {
        var mail = Composer(invite: "https://app.example.com/invite?lang=pl").Compose(MailKind.Invitation, "worker@acme.test", Token, "Acme");

        Assert.Contains($"https://app.example.com/invite?lang=pl&token={Token}", mail.TextBody);
    }

    [Fact]
    public void An_invitation_without_its_company_cannot_be_composed()
    {
        Assert.Throws<ArgumentException>(() => Composer().Compose(MailKind.Invitation, "worker@acme.test", Token));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public void Every_language_has_every_text_of_an_invitation(string locale)
    {
        var text = MailTexts.For(locale, MailKind.Invitation);

        Assert.All(
            new[] { text.Subject, text.Greeting, text.Intro, text.Button, text.Validity, text.LinkHint, text.Ignore },
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Contains("{0}", text.Subject);
        Assert.DoesNotContain("{1}", text.Subject);   // no header carries what a member typed
        Assert.Contains("{1}", text.Intro);
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pl", false)]
    [InlineData("en", true)]
    [InlineData("pl", true)]
    public void No_text_of_an_invitation_has_a_place_for_the_role(string locale, bool privateAccount)   // Toviq #724
    {
        var text = privateAccount ? MailTexts.InvitationToPrivateAccount(locale) : MailTexts.For(locale, MailKind.Invitation);

        Assert.All(
            new[] { text.Subject, text.Greeting, text.Intro, text.Button, text.Validity, text.LinkHint, text.Ignore, text.Paragraph ?? "" },
            value => Assert.DoesNotContain("{2}", value, StringComparison.Ordinal));
    }

    [Fact]
    public void The_other_mails_do_not_take_a_company_into_account()
    {
        var mail = Composer().Compose(MailKind.PasswordReset, "user@example.com", Token, "Acme");

        Assert.DoesNotContain("Acme", mail.TextBody);
        Assert.Contains("https://app.example.com/reset?token=", mail.TextBody);
    }
}
