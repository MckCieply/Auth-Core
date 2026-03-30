using Auth.Server.Email;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class CompleteSignupSettingsTests
{
    private static Dictionary<string, string?> Valid() => new()
    {
        [MailSettingsLoader.AppNameKey] = "speech-to-mail",
        [MailSettingsLoader.LocaleKey] = "pl",
        [MailSettingsLoader.ResetPasswordUrlKey] = "https://app.example.com/reset",
        [MailSettingsLoader.VerifyEmailUrlKey] = "https://app.example.com/verify",
        [MailSettingsLoader.AcceptInviteUrlKey] = "https://app.example.com/invite",
        [MailSettingsLoader.FromKey] = "no-reply@example.com",
        [MailSettingsLoader.SmtpHostKey] = "smtp.example.com",
        [MailSettingsLoader.SmtpPortKey] = "587",
        [MailSettingsLoader.SmtpSecurityKey] = "starttls",
    };

    private static MailSettings Load(Dictionary<string, string?> values, bool isDevelopment = false) =>
        MailSettingsLoader.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), isDevelopment);

    [Fact]
    public void The_key_is_the_one_of_the_contract()
    {
        Assert.Equal("Auth:App:FrontendUrls:CompleteSignup", MailSettingsLoader.CompleteSignupUrlKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_address_is_optional_and_blank_counts_as_unset(string? value)   // criteria 2, 17
    {
        var values = Valid();
        values[MailSettingsLoader.CompleteSignupUrlKey] = value;

        Assert.Null(Load(values).CompleteSignupUrl);
    }

    [Fact]
    public void A_valid_address_loads()
    {
        var values = Valid();
        values[MailSettingsLoader.CompleteSignupUrlKey] = "https://app.example.com/complete-signup";

        Assert.Equal(new Uri("https://app.example.com/complete-signup"), Load(values).CompleteSignupUrl);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/complete-signup")]
    [InlineData("ftp://app.example.com/complete-signup")]
    [InlineData("https://app.example.com/complete-signup#top")]
    [InlineData("https://user:secret@app.example.com/complete-signup")]
    [InlineData("https://app.example.com/complete-signup?token=abc")]
    [InlineData("https://app.example.com/complete-signup?a=1&token=abc")]
    public void A_bad_address_is_refused_naming_the_key_and_never_echoing_the_value(string value)   // criterion 17
    {
        var values = Valid();
        values[MailSettingsLoader.CompleteSignupUrlKey] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values, isDevelopment: true));

        Assert.Contains(MailSettingsLoader.CompleteSignupUrlKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Outside_Development_the_address_must_be_https()   // criterion 17: the rules of the other addresses
    {
        var values = Valid();
        values[MailSettingsLoader.CompleteSignupUrlKey] = "http://app.example.com/complete-signup";

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values, isDevelopment: false));

        Assert.Contains(MailSettingsLoader.CompleteSignupUrlKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(new Uri("http://app.example.com/complete-signup"), Load(values, isDevelopment: true).CompleteSignupUrl);
    }

    [Fact]
    public void The_other_three_addresses_stay_required()
    {
        var values = Valid();
        values[MailSettingsLoader.CompleteSignupUrlKey] = "https://app.example.com/complete-signup";
        values.Remove(MailSettingsLoader.AcceptInviteUrlKey);

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains(MailSettingsLoader.AcceptInviteUrlKey, ex.Message, StringComparison.Ordinal);
    }
}
