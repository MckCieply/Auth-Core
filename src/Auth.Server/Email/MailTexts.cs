using Auth.Infrastructure.Persistence;

namespace Auth.Server.Email;

/// <summary>
/// The texts of one mail. <see cref="Subject"/> and <see cref="Intro"/> hold <c>{0}</c> for the application name; an
/// invitation's <see cref="Intro"/> also holds <c>{1}</c> for the company and <c>{2}</c> for the role, and a sign-up's <c>{1}</c> for the company.
/// People type those (members, or a visitor signing up), so no subject uses them: nothing a person typed goes into a mail header.
/// </summary>
/// <param name="Paragraph">
/// A paragraph after the intro, or none. The invitation to an address that has a private account (spec 0010) has one; it holds <c>{0}</c> for
/// the application and <c>{1}</c> for the company.
/// </param>
public sealed record MailText(
    string Subject, string Greeting, string Intro, string Button, string Validity, string LinkHint, string Ignore, string? Paragraph = null);

/// <summary>
/// The texts of the mail "you already have an account" (spec 0009). It has no link and no button: <see cref="Subject"/>, <see cref="Intro"/>
/// and <see cref="SignInHint"/> hold <c>{0}</c> for the application name. Nothing a visitor typed goes into it.
/// </summary>
/// <param name="Deliberate">The sentence of spec 0010, after the sign-in hint: how to get a new account on purpose.</param>
public sealed record AccountExistsText(string Subject, string Greeting, string Intro, string SignInHint, string Deliberate, string Ignore);

/// <summary>
/// The texts of a notice (spec 0012 → Mails). No link and no button. <c>{0}</c> is the application, <c>{1}</c> the provider's display name,
/// <c>{2}</c> the account's address (in the body only: a header names the application and the provider, never the address).
/// </summary>
/// <param name="NoLink">Says that the mail holds no link, so that a copy of it that holds one is known for a forgery.</param>
public sealed record NoticeText(string Subject, string Greeting, string Intro, string Advice, string NoLink);

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
        ("en", MailKind.Invitation) => new MailText(
            Subject: "You are invited to {0}",
            Greeting: "Hello,",
            Intro: "You have been invited to join {1} on {0} as {2}. Use the link below to choose a password and accept the invitation.",
            Button: "Accept the invitation",
            Validity: "The link is valid for 7 days and works once.",
            LinkHint: "If the button does not work, copy this address into your browser:",
            Ignore: "If you did not expect this invitation, you can ignore this message. Nothing changes until the link is used."),
        ("pl", MailKind.Invitation) => new MailText(
            Subject: "Zaproszenie do {0}",
            Greeting: "Dzień dobry,",
            Intro: "Zaproszono Cię do firmy {1} w {0} jako {2}. Użyj poniższego linku, aby ustawić hasło i przyjąć zaproszenie.",
            Button: "Przyjmij zaproszenie",
            Validity: "Link jest ważny przez 7 dni i działa jeden raz.",
            LinkHint: "Jeśli przycisk nie działa, skopiuj ten adres do przeglądarki:",
            Ignore: "Jeśli nie spodziewałeś się tego zaproszenia, zignoruj tę wiadomość. Nic się nie zmieni, dopóki link nie zostanie użyty."),
        ("en", MailKind.Signup) => new MailText(
            Subject: "Complete your sign-up for {0}",
            Greeting: "Hello,",
            Intro: "Someone asked to create the company {1} on {0} with this email address. If it was you, use the link below to finish signing up.",
            Button: "Finish signing up",
            Validity: "The link is valid for 24 hours and works once.",
            LinkHint: "If the button does not work, copy this address into your browser:",
            Ignore: "If you did not ask for this, you can ignore this message. Nothing changes until the link is used."),
        ("pl", MailKind.Signup) => new MailText(
            Subject: "Dokończ rejestrację w {0}",
            Greeting: "Dzień dobry,",
            Intro: "Ktoś, podając ten adres e-mail, poprosił o utworzenie firmy {1} w {0}. Jeśli to Ty, użyj poniższego linku, aby dokończyć rejestrację.",
            Button: "Dokończ rejestrację",
            Validity: "Link jest ważny przez 24 godziny i działa jeden raz.",
            LinkHint: "Jeśli przycisk nie działa, skopiuj ten adres do przeglądarki:",
            Ignore: "Jeśli to nie Ty, zignoruj tę wiadomość. Nic się nie zmieni, dopóki link nie zostanie użyty."),
        _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No mail text for locale '{locale}' and kind '{kind}'."),
    };

    /// <summary>The mail an address gets when someone signs up with it and it has an account, of either kind (spec 0009 → Mails; spec 0010 → Mails).</summary>
    public static AccountExistsText AccountExists(string locale) => locale switch
    {
        "en" => new AccountExistsText(
            Subject: "You already have an account on {0}",
            Greeting: "Hello,",
            Intro: "Someone, perhaps you, tried to sign up for {0} with this email address. An account with this address already exists, so no new account was made.",
            SignInHint: "To use your account, sign in on the sign-in screen of {0}. If you forgot your password, use \"forgot password\" on that screen to choose a new one.",
            Deliberate: "If you want a new account on purpose, delete the one you have first, or sign up with another email address.",
            Ignore: "If it was not you, you can ignore this message. Nothing has changed."),
        "pl" => new AccountExistsText(
            Subject: "Masz już konto w {0}",
            Greeting: "Dzień dobry,",
            Intro: "Ktoś, być może Ty, próbował zarejestrować się w {0}, podając ten adres e-mail. Konto z tym adresem już istnieje, więc nie utworzono nowego.",
            SignInHint: "Aby skorzystać ze swojego konta, zaloguj się na ekranie logowania {0}. Jeśli nie pamiętasz hasła, skorzystaj na tym ekranie z opcji ustawienia nowego hasła.",
            Deliberate: "Jeśli celowo chcesz założyć nowe konto, najpierw usuń dotychczasowe albo zarejestruj się z innym adresem e-mail.",
            Ignore: "Jeśli to nie Ty, zignoruj tę wiadomość. Nic się nie zmieniło."),
        _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No mail text for locale '{locale}'."),
    };

    /// <summary>
    /// "Complete your sign-up" for a private person (spec 0010 → Mails): the subject, greeting, button, validity, link hint and ignore line of
    /// spec 0009, with an intro that names no company (<c>{0}</c> is the application).
    /// </summary>
    public static MailText PrivateSignup(string locale) => locale switch
    {
        "en" => For(locale, MailKind.Signup) with
        {
            Intro = "Someone asked to create a private account on {0} with this email address. If it was you, use the link below to finish signing up.",
        },
        "pl" => For(locale, MailKind.Signup) with
        {
            Intro = "Ktoś, podając ten adres e-mail, poprosił o założenie konta prywatnego w {0}. Jeśli to Ty, użyj poniższego linku, aby dokończyć rejestrację.",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No mail text for locale '{locale}'."),
    };

    /// <summary>
    /// The invitation to an address that has a private account (spec 0010 → Invitations): the intro's last sentence asks no password, and a
    /// paragraph says what accepting does. <c>{0}</c> the application, <c>{1}</c> the company, <c>{2}</c> the role.
    /// </summary>
    public static MailText InvitationToPrivateAccount(string locale) => locale switch
    {
        "en" => For(locale, MailKind.Invitation) with
        {
            Intro = "You have been invited to join {1} on {0} as {2}. Use the link below to accept the invitation.",
            Paragraph = "You already have a private account on {0} with this email address. If you accept, you move to {1}: the space of your private "
                + "account is deleted, with everything in it. Your account itself stays, with its email address and its password, and becomes a member of {1}.",
        },
        "pl" => For(locale, MailKind.Invitation) with
        {
            Intro = "Zaproszono Cię do firmy {1} w {0} jako {2}. Użyj poniższego linku, aby przyjąć zaproszenie.",
            Paragraph = "Masz już konto prywatne w {0} z tym adresem e-mail. Jeśli przyjmiesz zaproszenie, przejdziesz do firmy {1}: przestrzeń Twojego "
                + "konta prywatnego zostanie usunięta razem z całą zawartością. Samo konto zostaje, z tym samym adresem e-mail i hasłem, i stanie się kontem "
                + "członka firmy {1}.",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No mail text for locale '{locale}'."),
    };

    /// <summary>The notices of spec 0012 → Mails, Polish and English.</summary>
    public static NoticeText Notice(string locale, MailKind kind) => (locale, kind) switch
    {
        ("en", MailKind.ProviderLinked) => new NoticeText(
            Subject: "{1} was connected to your {0} account",
            Greeting: "Hello,",
            Intro: "{1} was connected to your {0} account {2}. From now on it signs you in.",
            Advice: "If it was not you, reset your password with \"Forgot your password?\" on the sign-in screen of {0}, then disconnect {1} in your settings.",
            NoLink: "This message holds no link: to act on it, open {0} yourself."),
        ("en", MailKind.ProviderUnlinked) => new NoticeText(
            Subject: "{1} was disconnected from your {0} account",
            Greeting: "Hello,",
            Intro: "{1} was disconnected from your {0} account {2}. It no longer signs you in, and your other devices were signed out.",
            Advice: "If it was not you, reset your password with \"Forgot your password?\" on the sign-in screen of {0} and check your settings.",
            NoLink: "This message holds no link: to act on it, open {0} yourself."),
        ("en", MailKind.PasswordChanged) => new NoticeText(
            Subject: "The password of your {0} account was changed",
            Greeting: "Hello,",
            Intro: "The password of your {0} account {2} was changed, and your other devices were signed out.",
            Advice: "If it was not you, reset it with \"Forgot your password?\" on the sign-in screen of {0}.",
            NoLink: "This message holds no link: to act on it, open {0} yourself."),
        ("pl", MailKind.ProviderLinked) => new NoticeText(
            Subject: "Do Twojego konta w {0} podłączono {1}",
            Greeting: "Dzień dobry,",
            Intro: "Do Twojego konta {2} w {0} podłączono {1}. Od teraz możesz się nim logować.",
            Advice: "Jeśli to nie Ty, ustaw nowe hasło, korzystając z opcji ustawienia nowego hasła na ekranie logowania {0}, a potem odłącz {1} w ustawieniach.",
            NoLink: "Ta wiadomość nie zawiera linku: aby coś zmienić, otwórz {0} samodzielnie."),
        ("pl", MailKind.ProviderUnlinked) => new NoticeText(
            Subject: "Od Twojego konta w {0} odłączono {1}",
            Greeting: "Dzień dobry,",
            Intro: "Od Twojego konta {2} w {0} odłączono {1}. Nie służy już do logowania, a Twoje pozostałe urządzenia zostały wylogowane.",
            Advice: "Jeśli to nie Ty, ustaw nowe hasło, korzystając z opcji ustawienia nowego hasła na ekranie logowania {0}, i sprawdź swoje ustawienia.",
            NoLink: "Ta wiadomość nie zawiera linku: aby coś zmienić, otwórz {0} samodzielnie."),
        ("pl", MailKind.PasswordChanged) => new NoticeText(
            Subject: "Hasło do Twojego konta w {0} zostało zmienione",
            Greeting: "Dzień dobry,",
            Intro: "Hasło do Twojego konta {2} w {0} zostało zmienione, a Twoje pozostałe urządzenia zostały wylogowane.",
            Advice: "Jeśli to nie Ty, ustaw nowe hasło, korzystając z opcji ustawienia nowego hasła na ekranie logowania {0}.",
            NoLink: "Ta wiadomość nie zawiera linku: aby coś zmienić, otwórz {0} samodzielnie."),
        _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No notice text for locale '{locale}' and kind '{kind}'."),
    };
}
