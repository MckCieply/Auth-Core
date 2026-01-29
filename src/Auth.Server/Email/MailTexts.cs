using Auth.Infrastructure.Persistence;

namespace Auth.Server.Email;

/// <summary>The texts of one mail. <see cref="Subject"/> and <see cref="Intro"/> hold <c>{0}</c> for the application name.</summary>
public sealed record MailText(string Subject, string Greeting, string Intro, string Button, string Validity, string LinkHint, string Ignore);

/// <summary>
/// The mail texts, Polish and English. Plain tables rather than <c>.resx</c> satellites: the container image runs
/// without ICU, where a culture such as <c>pl</c> cannot be created, so resources could not be selected by culture.
/// </summary>
public static class MailTexts
{
    public static MailText For(string locale, MailKind kind) => (locale, kind) switch
    {
        ("en", MailKind.PasswordReset) => new MailText(
            Subject: "Reset your {0} password",
            Greeting: "Hello,",
            Intro: "We received a request to reset the password of your {0} account. Use the link below to choose a new password.",
            Button: "Choose a new password",
            Validity: "The link is valid for 1 hour and works once.",
            LinkHint: "If the button does not work, copy this address into your browser:",
            Ignore: "If you did not ask for this, you can ignore this message. Nothing changes until the link is used."),
        ("en", MailKind.EmailVerification) => new MailText(
            Subject: "Confirm your email address for {0}",
            Greeting: "Hello,",
            Intro: "Confirm that this email address belongs to your {0} account by using the link below.",
            Button: "Confirm email address",
            Validity: "The link is valid for 24 hours and works once.",
            LinkHint: "If the button does not work, copy this address into your browser:",
            Ignore: "If you did not ask for this, you can ignore this message. Nothing changes until the link is used."),
        ("pl", MailKind.PasswordReset) => new MailText(
            Subject: "Ustaw nowe hasło w {0}",
            Greeting: "Dzień dobry,",
            Intro: "Otrzymaliśmy prośbę o zmianę hasła do Twojego konta w {0}. Użyj poniższego linku, aby ustawić nowe hasło.",
            Button: "Ustaw nowe hasło",
            Validity: "Link jest ważny przez 1 godzinę i działa jeden raz.",
            LinkHint: "Jeśli przycisk nie działa, skopiuj ten adres do przeglądarki:",
            Ignore: "Jeśli to nie Ty, zignoruj tę wiadomość. Nic się nie zmieni, dopóki link nie zostanie użyty."),
        ("pl", MailKind.EmailVerification) => new MailText(
            Subject: "Potwierdź adres e-mail w {0}",
            Greeting: "Dzień dobry,",
            Intro: "Potwierdź, że ten adres e-mail należy do Twojego konta w {0}, używając poniższego linku.",
            Button: "Potwierdź adres e-mail",
            Validity: "Link jest ważny przez 24 godziny i działa jeden raz.",
            LinkHint: "Jeśli przycisk nie działa, skopiuj ten adres do przeglądarki:",
            Ignore: "Jeśli to nie Ty, zignoruj tę wiadomość. Nic się nie zmieni, dopóki link nie zostanie użyty."),
        _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No mail text for locale '{locale}' and kind '{kind}'."),
    };
}
