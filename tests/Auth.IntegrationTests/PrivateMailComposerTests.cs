using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Mails and Invitations: the new texts, in English and Polish, HTML and text.</summary>
public sealed class PrivateMailComposerTests
{
    private const string Token = "abcDEF123_-abcDEF123_-abcDEF123_-abcDEF123_";
    private static readonly string[] Holes = ["{0}", "{1}", "{2}"];   // a field, not an inline array: CA1861 under -warnaserror

    private const string PrivateIntroEn = "Someone asked to create a private account on speech-to-mail with this email address. If it was you, use the link below to finish signing up.";
    private const string PrivateIntroPl = "Ktoś, podając ten adres e-mail, poprosił o założenie konta prywatnego w speech-to-mail. Jeśli to Ty, użyj poniższego linku, aby dokończyć rejestrację.";
    private const string DeliberateEn = "If you want a new account on purpose, delete the one you have first, or sign up with another email address.";
    private const string DeliberatePl = "Jeśli celowo chcesz założyć nowe konto, najpierw usuń dotychczasowe albo zarejestruj się z innym adresem e-mail.";

    private static MailComposer Composer(string locale = "en") =>
        new(new MailSettings
        {
            AppName = "speech-to-mail",
            Locale = locale,
            ResetPasswordUrl = new Uri("https://app.example.com/reset"),
            VerifyEmailUrl = new Uri("https://app.example.com/verify"),
            AcceptInviteUrl = new Uri("https://app.example.com/invite"),
            CompleteSignupUrl = new Uri("https://app.example.com/complete-signup"),
            FromAddress = "no-reply@example.com",
            Smtp = new SmtpSettings { Host = "smtp.invalid", Port = 587, Security = SmtpSecurity.StartTls },
        });

    private static void AssertHtmlHoldsTheText(ComposedMail mail)
    {
        var encoder = HtmlEncoder.Create(UnicodeRanges.All);
        Assert.All(
            mail.TextBody.Split("\r\n").Where(line => line.Length > 0),
            line => Assert.Contains(encoder.Encode(line), mail.HtmlBody, StringComparison.Ordinal));
    }

    private static void AssertParagraphBetweenIntroAndLink(ComposedMail mail, string introStart, string paragraphStart)
    {
        foreach (var body in new[] { mail.TextBody, mail.HtmlBody })
        {
            var intro = body.IndexOf(introStart, StringComparison.Ordinal);
            var paragraph = body.IndexOf(paragraphStart, StringComparison.Ordinal);
            var link = body.IndexOf("https://app.example.com/invite?token=", StringComparison.Ordinal);
            Assert.True(intro >= 0);
            Assert.True(intro < paragraph);
            Assert.True(paragraph < link);
        }
    }

    [Theory]
    [InlineData("en", PrivateIntroEn, "Complete your sign-up for speech-to-mail")]
    [InlineData("pl", PrivateIntroPl, "Dokończ rejestrację w speech-to-mail")]
    public void The_private_sign_up_mail_has_the_intro_without_a_company_and_the_link(string locale, string intro, string subject)   // criteria 4, 31
    {
        var mail = Composer(locale).Compose(MailKind.Signup, "ola@poczta.test", Token, privateAccount: true);

        Assert.Equal(subject, mail.Subject);
        Assert.Contains(intro, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://app.example.com/complete-signup?token=" + Token, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("href=\"https://app.example.com/complete-signup?token=" + Token + "\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("{", mail.Subject + mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
        AssertHtmlHoldsTheText(mail);
    }

    [Theory]
    [InlineData("en", DeliberateEn)]
    [InlineData("pl", DeliberatePl)]
    public void The_account_exists_mail_holds_the_new_sentence_after_the_sign_in_hint(string locale, string sentence)   // criteria 5, 31
    {
        var mail = Composer(locale).ComposeAccountExists("ola@poczta.test");

        Assert.Contains(sentence, mail.TextBody, StringComparison.Ordinal);
        var hint = string.Format(CultureInfo.InvariantCulture, MailTexts.AccountExists(locale).SignInHint, "speech-to-mail");
        var hintAt = mail.TextBody.IndexOf(hint, StringComparison.Ordinal);
        Assert.True(hintAt >= 0);
        Assert.True(hintAt < mail.TextBody.IndexOf(sentence, StringComparison.Ordinal));
        var encoder = HtmlEncoder.Create(UnicodeRanges.All);
        var hintAtInHtml = mail.HtmlBody.IndexOf(encoder.Encode(hint), StringComparison.Ordinal);
        Assert.True(hintAtInHtml >= 0);
        Assert.True(hintAtInHtml < mail.HtmlBody.IndexOf(encoder.Encode(sentence), StringComparison.Ordinal));
        Assert.DoesNotContain("://", mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
        AssertHtmlHoldsTheText(mail);
    }

    [Fact]
    public void The_invitation_to_a_private_account_in_english_has_the_changed_intro_and_the_paragraph()   // criteria 16, 31
    {
        var mail = Composer().Compose(MailKind.Invitation, "ola@poczta.test", Token, "Acme", "user", privateAccount: true);

        Assert.Contains("You have been invited to join Acme on speech-to-mail as user. Use the link below to accept the invitation.", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(
            "You already have a private account on speech-to-mail with this email address. If you accept, you move to Acme: the space of your "
            + "private account is deleted, with everything in it. Your account itself stays, with its email address and its password, and becomes a member of Acme.",
            mail.TextBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("choose a password", mail.TextBody, StringComparison.Ordinal);
        AssertHtmlHoldsTheText(mail);
        AssertParagraphBetweenIntroAndLink(mail, "You have been invited", "You already have a private account");
    }

    [Fact]
    public void The_invitation_to_a_private_account_in_polish_has_the_changed_intro_and_the_paragraph()   // criteria 16, 31
    {
        var mail = Composer("pl").Compose(MailKind.Invitation, "ola@poczta.test", Token, "Żółw i Spółka", "użytkownik", privateAccount: true);

        Assert.Contains("Zaproszono Cię do firmy Żółw i Spółka w speech-to-mail jako użytkownik. Użyj poniższego linku, aby przyjąć zaproszenie.", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(
            "Masz już konto prywatne w speech-to-mail z tym adresem e-mail. Jeśli przyjmiesz zaproszenie, przejdziesz do firmy Żółw i Spółka: przestrzeń "
            + "Twojego konta prywatnego zostanie usunięta razem z całą zawartością. Samo konto zostaje, z tym samym adresem e-mail i hasłem, i stanie się "
            + "kontem członka firmy Żółw i Spółka.",
            mail.TextBody,
            StringComparison.Ordinal);
        Assert.Contains("Żółw i Spółka", mail.HtmlBody, StringComparison.Ordinal);   // letters stay letters
        AssertHtmlHoldsTheText(mail);
        AssertParagraphBetweenIntroAndLink(mail, "Zaproszono Cię", "Masz już konto prywatne");
    }

    [Fact]
    public void The_company_and_the_role_are_encoded_in_the_html_of_the_paragraph_and_never_in_the_subject()   // criterion 31
    {
        var mail = Composer().Compose(MailKind.Invitation, "ola@poczta.test", Token, "Tom & <Jerry>", "<b>boss</b>", privateAccount: true);

        Assert.Contains("If you accept, you move to Tom & <Jerry>:", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("If you accept, you move to Tom &amp; &lt;Jerry&gt;:", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;boss&lt;/b&gt;", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<Jerry>", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Jerry", mail.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_other_invitation_is_the_mail_of_spec_0005()   // criterion 16
    {
        var mail = Composer().Compose(MailKind.Invitation, "ola@poczta.test", Token, "Acme", "user");

        Assert.Contains("Use the link below to choose a password and accept the invitation.", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private account", mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public void Every_language_has_every_new_text(string locale)   // criterion 31
    {
        var signup = MailTexts.PrivateSignup(locale);
        Assert.Contains("{0}", signup.Intro, StringComparison.Ordinal);
        Assert.DoesNotContain("{1}", signup.Intro, StringComparison.Ordinal);
        Assert.Null(signup.Paragraph);

        var invitation = MailTexts.InvitationToPrivateAccount(locale);
        Assert.All(Holes, hole => Assert.Contains(hole, invitation.Intro, StringComparison.Ordinal));
        Assert.NotNull(invitation.Paragraph);
        Assert.Contains("{0}", invitation.Paragraph, StringComparison.Ordinal);
        Assert.Contains("{1}", invitation.Paragraph, StringComparison.Ordinal);
        Assert.Equal(MailTexts.For(locale, MailKind.Invitation).Subject, invitation.Subject);

        Assert.False(string.IsNullOrWhiteSpace(MailTexts.AccountExists(locale).Deliberate));
    }

    [Fact]
    public void An_unknown_language_has_neither_new_text()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MailTexts.PrivateSignup("xx"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MailTexts.InvitationToPrivateAccount("xx"));
    }

    [Fact]
    public void A_private_sign_up_leaves_out_a_company_it_is_given()
    {
        var mail = Composer().Compose(MailKind.Signup, "ola@poczta.test", Token, "Acme", privateAccount: true);

        Assert.DoesNotContain("Acme", mail.Subject + mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(PrivateIntroEn, mail.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void A_private_sign_up_needs_no_company_and_an_organization_sign_up_still_does()
    {
        _ = Composer().Compose(MailKind.Signup, "ola@poczta.test", Token, privateAccount: true);

        Assert.Throws<ArgumentException>(() => Composer().Compose(MailKind.Signup, "ola@poczta.test", Token));
    }
}
