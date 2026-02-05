using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class MailSettingsTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string SmtpPassword = "smtp-secret-value";

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
        [MailSettingsLoader.SmtpUsernameKey] = "mailer",
        [MailSettingsLoader.SmtpPasswordKey] = SmtpPassword,
    };

    private static MailSettings Load(Dictionary<string, string?> values, bool isDevelopment = false) =>
        MailSettingsLoader.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), isDevelopment);

    [Fact]
    public void Valid_settings_load()
    {
        var settings = Load(Valid());

        Assert.Equal("speech-to-mail", settings.AppName);
        Assert.Equal("pl", settings.Locale);
        Assert.Equal(new Uri("https://app.example.com/reset"), settings.ResetPasswordUrl);
        Assert.Equal(new Uri("https://app.example.com/verify"), settings.VerifyEmailUrl);
        Assert.Equal(new Uri("https://app.example.com/invite"), settings.AcceptInviteUrl);
        Assert.Equal("no-reply@example.com", settings.FromAddress);
        Assert.Equal("smtp.example.com", settings.Smtp.Host);
        Assert.Equal(587, settings.Smtp.Port);
        Assert.Equal(SmtpSecurity.StartTls, settings.Smtp.Security);
        Assert.Equal("mailer", settings.Smtp.Username);
        Assert.Equal(SmtpPassword, settings.Smtp.Password);
    }

    [Theory]
    [InlineData(MailSettingsLoader.AppNameKey)]
    [InlineData(MailSettingsLoader.LocaleKey)]
    [InlineData(MailSettingsLoader.ResetPasswordUrlKey)]
    [InlineData(MailSettingsLoader.VerifyEmailUrlKey)]
    [InlineData(MailSettingsLoader.AcceptInviteUrlKey)]
    [InlineData(MailSettingsLoader.FromKey)]
    [InlineData(MailSettingsLoader.SmtpHostKey)]
    [InlineData(MailSettingsLoader.SmtpPortKey)]
    [InlineData(MailSettingsLoader.SmtpSecurityKey)]
    public void Missing_or_blank_value_is_refused_naming_the_key(string key)   // criterion 17
    {
        foreach (var value in new[] { null, "", "   " })
        {
            var values = Valid();
            values[key] = value;

            var ex = Assert.Throws<InvalidOperationException>(() => Load(values));
            Assert.Contains(key, ex.Message);
        }
    }

    [Theory]
    [InlineData(MailSettingsLoader.LocaleKey, "de")]
    [InlineData(MailSettingsLoader.LocaleKey, "PL-pl")]
    [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "/reset")]
    [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "ftp://app.example.com/reset")]
    [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "http://app.example.com/reset")]     // not https outside Development
    [InlineData(MailSettingsLoader.VerifyEmailUrlKey, "https://app.example.com/verify#top")] // a fragment would swallow the token
    [InlineData(MailSettingsLoader.AcceptInviteUrlKey, "http://app.example.com/invite")]       // not https outside Development
    [InlineData(MailSettingsLoader.AcceptInviteUrlKey, "https://app.example.com/invite?token=x")]   // the link adds its own
    [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "https://user:secret@app.example.com/reset")]
    [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "https://app.example.com/reset?token=x")]         // the link adds its own
    [InlineData(MailSettingsLoader.FromKey, "not an address")]
    [InlineData(MailSettingsLoader.FromKey, "postmaster")]
    [InlineData(MailSettingsLoader.FromKey, "a@example.com, b@example.com")]
    [InlineData(MailSettingsLoader.AppNameKey, "two\nlines")]
    [InlineData(MailSettingsLoader.SmtpPortKey, "0")]
    [InlineData(MailSettingsLoader.SmtpPortKey, "65536")]
    [InlineData(MailSettingsLoader.SmtpPortKey, "smtp")]
    [InlineData(MailSettingsLoader.SmtpSecurityKey, "ssl3")]
    [InlineData(MailSettingsLoader.SmtpSecurityKey, "none")]                                 // no TLS outside Development
    public void Invalid_value_is_refused_naming_the_key_and_not_the_value(string key, string value)   // criterion 17
    {
        var values = Valid();
        values[key] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values));
        Assert.Contains(key, ex.Message);
        Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public void Development_accepts_plain_http_links_and_an_unencrypted_mail_server()
    {
        var values = Valid();
        values[MailSettingsLoader.ResetPasswordUrlKey] = "http://localhost:4200/reset";
        values[MailSettingsLoader.SmtpSecurityKey] = "none";

        var settings = Load(values, isDevelopment: true);

        Assert.Equal(SmtpSecurity.None, settings.Smtp.Security);
    }

    [Fact]
    public void Credentials_are_optional_but_come_as_a_pair()
    {
        var none = Valid();
        none.Remove(MailSettingsLoader.SmtpUsernameKey);
        none.Remove(MailSettingsLoader.SmtpPasswordKey);
        Assert.Null(Load(none).Smtp.Username);

        var half = Valid();
        half.Remove(MailSettingsLoader.SmtpUsernameKey);
        var ex = Assert.Throws<InvalidOperationException>(() => Load(half));
        Assert.Contains(MailSettingsLoader.SmtpUsernameKey, ex.Message);
        Assert.DoesNotContain(SmtpPassword, ex.Message);
    }

    [Fact]
    public void Settings_do_not_print_the_password()
    {
        var settings = Load(Valid());

        Assert.DoesNotContain(SmtpPassword, settings.ToString());
        Assert.DoesNotContain(SmtpPassword, settings.Smtp.ToString());
    }

    [Fact]
    public async Task Host_does_not_start_without_mail_settings()   // criterion 17
    {
        // Production: in Development appsettings.Development.json supplies a sender of its own.
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithEnvironment("Production")
            .WithoutSetting(MailSettingsLoader.FromKey);

        // A machine-wide Auth__Email__From would fill the gap: hide it while the host is built.
        var variable = MailSettingsLoader.FromKey.Replace(":", "__", StringComparison.Ordinal);
        var saved = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, null);
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.Contains(MailSettingsLoader.FromKey, ex.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, saved);
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Host_starts_on_the_test_defaults_in_every_environment(string environment)
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment(environment);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/auth/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
