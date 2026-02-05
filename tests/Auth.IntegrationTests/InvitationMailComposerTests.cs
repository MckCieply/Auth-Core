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
    public void Invitation_in_english_names_the_application_the_company_the_role_the_link_and_the_lifetime()   // criterion 2
    {
        var mail = Composer().Compose(MailKind.Invitation, "worker@acme.test", Token, "Acme", "user");

        Assert.Equal("worker@acme.test", mail.To);
        Assert.Equal("You are invited to speech-to-mail", mail.Subject);
        Assert.Contains("speech-to-mail", mail.TextBody);
        Assert.Contains("Acme", mail.TextBody);
        Assert.Contains("as user", mail.TextBody);
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
        var mail = Composer("pl").Compose(MailKind.Invitation, "worker@acme.test", Token, "Żółw i Spółka", "kierownik");

        Assert.Equal("Zaproszenie do speech-to-mail", mail.Subject);
        Assert.Contains("Żółw i Spółka", mail.TextBody);
        Assert.Contains("jako kierownik", mail.TextBody);
        Assert.Contains($"https://app.example.com/invite?token={Token}", mail.TextBody);
        Assert.Contains("ważny przez 7 dni", mail.TextBody);
        Assert.Contains("zignoruj tę wiadomość", mail.TextBody);
        Assert.Contains("Żółw i Spółka", mail.HtmlBody);      // letters stay letters, not entities
        Assert.Contains("ważny przez 7 dni", mail.HtmlBody);
        Assert.Contains("<html lang=\"pl\">", mail.HtmlBody);
    }

    [Fact]
    public void Company_and_role_names_are_encoded_in_the_html_part_and_never_in_a_header()   // criterion 23
    {
        var mail = Composer().Compose(MailKind.Invitation, "worker@acme.test", Token, "Tom & <Jerry>", "<b>boss</b>");

        Assert.Contains("Tom & <Jerry>", mail.TextBody);
        Assert.Contains("Tom &amp; &lt;Jerry&gt;", mail.HtmlBody);
        Assert.Contains("&lt;b&gt;boss&lt;/b&gt;", mail.HtmlBody);
        Assert.DoesNotContain("<Jerry>", mail.HtmlBody);
        Assert.DoesNotContain("<b>boss</b>", mail.HtmlBody);
        // The subject is a header: the names a member typed are not in it, nor in the recipient.
        Assert.Equal("You are invited to speech-to-mail", mail.Subject);
        Assert.DoesNotContain("Jerry", mail.Subject + mail.To);
        Assert.DoesNotContain("boss", mail.Subject + mail.To);
    }

    [Fact]
    public void A_name_that_looks_like_a_format_placeholder_is_printed_as_it_is()
    {
        var mail = Composer().Compose(MailKind.Invitation, "worker@acme.test", Token, "{0}{1}{2}", "{2}");

        Assert.Contains("{0}{1}{2}", mail.TextBody);
        Assert.Contains("as {2}.", mail.TextBody);
    }

    [Fact]
    public void Link_keeps_a_query_string_the_invitation_screen_already_has()
    {
        var mail = Composer(invite: "https://app.example.com/invite?lang=pl").Compose(MailKind.Invitation, "worker@acme.test", Token, "Acme", "user");

        Assert.Contains($"https://app.example.com/invite?lang=pl&token={Token}", mail.TextBody);
    }

    [Fact]
    public void An_invitation_without_its_company_or_role_cannot_be_composed()
    {
        Assert.Throws<ArgumentException>(() => Composer().Compose(MailKind.Invitation, "worker@acme.test", Token));
        Assert.Throws<ArgumentException>(() => Composer().Compose(MailKind.Invitation, "worker@acme.test", Token, "Acme"));
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
        Assert.DoesNotContain("{2}", text.Subject);
        Assert.Contains("{1}", text.Intro);
        Assert.Contains("{2}", text.Intro);
    }

    [Fact]
    public void The_other_mails_do_not_take_a_company_or_a_role_into_account()
    {
        var mail = Composer().Compose(MailKind.PasswordReset, "user@example.com", Token, "Acme", "admin");

        Assert.DoesNotContain("Acme", mail.TextBody);
        Assert.Contains("https://app.example.com/reset?token=", mail.TextBody);
    }
}
