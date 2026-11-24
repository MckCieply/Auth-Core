using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → Mails: the notices in the instance's language, through the dispatcher.</summary>
public sealed class SettingsMailPolishTests : ProviderTestBase
{
    public SettingsMailPolishTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(MailSettingsLoader.LocaleKey, "pl");
    }

    private async Task QueueAsync(MailKind kind, string? provider)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        Assert.True(await Factory.Services.GetRequiredService<MailNotices>()
            .QueueAsync(db, kind, "OLA.NOWAK@FIRMA.TEST", provider, StorableTime.Now(Clock), TestContext.Current.CancellationToken));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_provider_notice_and_a_password_notice_are_in_polish_with_the_stored_address_and_the_display_name()   // criterion 22
    {
        await CreateUserAsync("Ola.Nowak@Firma.test", confirmed: true);
        await QueueAsync(MailKind.ProviderLinked, "github");
        await QueueAsync(MailKind.PasswordChanged, null);

        Assert.Equal(2, await DispatchAsync());

        var linked = Assert.Single(Mail.Sent, m => m.Subject.Contains("podłączono", StringComparison.Ordinal));
        Assert.Equal("Do Twojego konta w " + AuthAppFactory.DefaultAppName + " podłączono GitHub", linked.Subject);
        Assert.Equal("Ola.Nowak@Firma.test", linked.To);
        Assert.Contains("Do Twojego konta Ola.Nowak@Firma.test w " + AuthAppFactory.DefaultAppName + " podłączono GitHub.", linked.TextBody, StringComparison.Ordinal);
        var changed = Assert.Single(Mail.Sent, m => m.Subject.Contains("zostało zmienione", StringComparison.Ordinal));
        Assert.Equal("Hasło do Twojego konta w " + AuthAppFactory.DefaultAppName + " zostało zmienione", changed.Subject);
        Assert.Contains("Ola.Nowak@Firma.test", changed.TextBody, StringComparison.Ordinal);
    }
}
