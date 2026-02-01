using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

public sealed class SmtpMailTransportTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    private const string Token = "abcDEF123_-abcDEF123_-abcDEF123_-abcDEF123_";

    private MailSettings Settings(int? port = null) => new()
    {
        AppName = "speech-to-mail",
        Locale = "pl",
        ResetPasswordUrl = new Uri("https://app.example.com/reset"),
        VerifyEmailUrl = new Uri("https://app.example.com/verify"),
        FromAddress = "no-reply@mail.example.com",
        Smtp = new SmtpSettings { Host = mailpit.Host, Port = port ?? mailpit.SmtpPort, Security = SmtpSecurity.None },
    };

    private static string Recipient() => $"anna-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Mail_arrives_with_both_parts_and_diacritics_intact()   // criterion 15
    {
        var settings = Settings();
        var to = Recipient();
        var mail = new MailComposer(settings).Compose(MailKind.EmailVerification, to, Token);

        await new SmtpMailTransport(settings, TimeProvider.System).SendAsync(mail, TestContext.Current.CancellationToken);

        var received = await mailpit.WaitForMailAsync(to);
        Assert.Equal("Potwierdź adres e-mail w speech-to-mail", received.Subject);
        Assert.Contains("Dzień dobry,", received.Text);
        Assert.Contains($"https://app.example.com/verify?token={Token}", received.Text);
        Assert.Contains("Jeśli to nie Ty, zignoruj tę wiadomość.", received.Text);
        Assert.Contains("Potwierdź adres e-mail", received.Html);
        Assert.Contains($"https://app.example.com/verify?token={Token}", received.Html);
        Assert.Equal("no-reply@mail.example.com", received.FromAddress);
        Assert.Equal("speech-to-mail", received.FromName);
        Assert.Contains("multipart/alternative", received.Raw);
    }

    [Fact]
    public async Task Message_id_is_made_from_the_sender_domain_not_the_machine_name()
    {
        var settings = Settings();
        var to = Recipient();
        var mail = new MailComposer(settings).Compose(MailKind.PasswordReset, to, Token);

        await new SmtpMailTransport(settings, TimeProvider.System).SendAsync(mail, TestContext.Current.CancellationToken);

        var received = await mailpit.WaitForMailAsync(to);
        var messageId = received.Raw.Split("\r\n").Single(line => line.StartsWith("Message-Id:", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("@mail.example.com>", messageId);
        Assert.DoesNotContain(Environment.MachineName, received.Raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Server_that_does_not_answer_is_an_exception()
    {
        // Port 1 on the container host: nothing listens there.
        var settings = Settings(port: 1);
        var mail = new MailComposer(settings).Compose(MailKind.PasswordReset, Recipient(), Token);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new SmtpMailTransport(settings, TimeProvider.System).SendAsync(mail, TestContext.Current.CancellationToken));
    }
}
