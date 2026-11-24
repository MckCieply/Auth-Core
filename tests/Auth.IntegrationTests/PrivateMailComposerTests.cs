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
    private static readonly string[] Holes = ["{0}", "{1}"];   // a field, not an inline array: CA1861 under -warnaserror

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
    public void The_invitation_to_an_existing_account_in_english_has_the_intro_and_the_paragraph()   // criteria 21, 31
    {
        var mail = Composer().Compose(MailKind.Invitation, "ola@poczta.test", Token, "Acme", accountExists: true);

        Assert.Contains("You have been invited to join Acme on speech-to-mail. Use the link below to accept the invitation.", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(
            "You already have an account on speech-to-mail with this email address. If you accept, your account also joins Acme and keeps "
            + "everything it has now, with the same email address and the same way of signing in.",
            mail.TextBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("choose a password", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private account on", mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
        AssertHtmlHoldsTheText(mail);
        AssertParagraphBetweenIntroAndLink(mail, "You have been invited", "You already have an account");
    }

    [Fact]
    public void The_invitation_to_an_existing_account_in_polish_has_the_intro_and_the_paragraph()   // criteria 21, 31
    {
        var mail = Composer("pl").Compose(MailKind.Invitation, "ola@poczta.test", Token, "Żółw i Spółka", accountExists: true);

        Assert.Contains("Zaproszono Cię do firmy Żółw i Spółka w speech-to-mail. Użyj poniższego linku, aby przyjąć zaproszenie.", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(
            "Masz już konto w speech-to-mail z tym adresem e-mail. Jeśli przyjmiesz zaproszenie, Twoje konto dołączy także do firmy Żółw i Spółka i zachowa wszystko, co ma teraz, z tym samym adresem e-mail i tym samym sposobem logowania.",
            mail.TextBody,
            StringComparison.Ordinal);
        Assert.Contains("Żółw i Spółka", mail.HtmlBody, StringComparison.Ordinal);   // letters stay letters
        AssertHtmlHoldsTheText(mail);
        AssertParagraphBetweenIntroAndLink(mail, "Zaproszono Cię", "Masz już konto w");
    }

    [Fact]
    public void The_company_is_encoded_in_the_html_of_the_paragraph_and_never_in_the_subject()   // criterion 31
    {
        var mail = Composer().Compose(MailKind.Invitation, "ola@poczta.test", Token, "Tom & <Jerry>", accountExists: true);

        Assert.Contains("If you accept, your account also joins Tom & <Jerry> and keeps", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("If you accept, your account also joins Tom &amp; &lt;Jerry&gt; and keeps", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<Jerry>", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Jerry", mail.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_other_invitation_is_the_mail_of_spec_0005()   // criterion 21
    {
        var mail = Composer().Compose(MailKind.Invitation, "ola@poczta.test", Token, "Acme", accountExists: false);

        Assert.Contains("Use the link below to choose a password and accept the invitation.", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("account on", mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void The_private_flag_no_longer_changes_an_invitation()   // criterion 21
    {
        var plain = Composer().Compose(MailKind.Invitation, "ola@poczta.test", Token, "Acme");
        var flagged = Composer().Compose(MailKind.Invitation, "ola@poczta.test", Token, "Acme", privateAccount: true);

        Assert.Equal(plain.TextBody, flagged.TextBody);
        Assert.Equal(plain.HtmlBody, flagged.HtmlBody);
        Assert.Contains("Use the link below to choose a password and accept the invitation.", flagged.TextBody, StringComparison.Ordinal);
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

        var invitation = MailTexts.InvitationToExistingAccount(locale);
        Assert.All(Holes, hole => Assert.Contains(hole, invitation.Intro, StringComparison.Ordinal));
        Assert.DoesNotContain("{2}", invitation.Intro, StringComparison.Ordinal);
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
        Assert.Throws<ArgumentOutOfRangeException>(() => MailTexts.InvitationToExistingAccount("xx"));
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
