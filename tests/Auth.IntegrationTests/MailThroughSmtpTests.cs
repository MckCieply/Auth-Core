using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Seeding;

namespace Auth.IntegrationTests;

public sealed class MailThroughSmtpTests(PostgresFixture postgres, KeyMaterialFixture keys, MailpitFixture mailpit)
    : IClassFixture<MailpitFixture>
{
    private AuthAppFactory Host(string seed) =>
        new AuthAppFactory(postgres, keys)
            .WithSetting(DevUserSeeder.EmailKey, seed)
            .WithSetting(MailSettingsLoader.LocaleKey, "pl")
            .WithSetting(MailSettingsLoader.SmtpHostKey, mailpit.Host)
            .WithSetting(MailSettingsLoader.SmtpPortKey, mailpit.SmtpPort.ToString(CultureInfo.InvariantCulture))
            .WithSetting(MailSettingsLoader.SmtpSecurityKey, "none");

    [Fact]
    public async Task Forgot_request_ends_as_a_mail_on_the_mail_server()   // criteria 1, 15
    {
        // An address of its own: the catcher is shared with the other SMTP tests.
        var seed = $"anna-{Guid.NewGuid():N}@example.com";
        await using var factory = Host(seed);
        using var client = factory.CreateClient();

        using var response = await AccountApi.Forgot(client, seed);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        var mail = await mailpit.WaitForMailAsync(seed);
        Assert.Equal("Ustaw nowe hasło w " + AuthAppFactory.DefaultAppName, mail.Subject);
        Assert.Matches(Regex.Escape(AuthAppFactory.DefaultResetUrl) + @"\?token=[A-Za-z0-9_-]{43}\s", mail.Text);
        Assert.Contains("Ustaw nowe hasło", mail.Html);
        Assert.Equal(AuthAppFactory.DefaultFrom, mail.FromAddress);
    }

    [Fact]
    public async Task Forgot_for_an_unknown_address_sends_nothing()   // criterion 2
    {
        var seed = $"anna-{Guid.NewGuid():N}@example.com";
        var unknown = $"nobody-{Guid.NewGuid():N}@example.com";
        await using var factory = Host(seed);
        using var client = factory.CreateClient();

        using var forUnknown = await AccountApi.Forgot(client, unknown);
        using var forSeed = await AccountApi.Forgot(client, seed);

        // The queue is handled in order: once the later mail is there, the earlier request has been handled too.
        await mailpit.WaitForMailAsync(seed);
        Assert.Equal(0, await mailpit.CountAsync(unknown));
    }
}
