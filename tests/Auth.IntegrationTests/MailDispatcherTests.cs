using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

public sealed class MailDispatcherTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private static readonly TimeSpan PastTheLimit = TimeSpan.FromSeconds(61);

    private Task<List<MailRequest>> QueueAsync() =>
        InDbAsync(db => db.MailRequests.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    private Task<List<EmailToken>> TokensAsync() =>
        InDbAsync(db => db.EmailTokens.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Request_for_an_account_sends_one_mail_and_stores_only_the_hash_of_its_token()   // criteria 1, 15
    {
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Factory.SeedEmail, mail.To);
        Assert.Contains(AuthAppFactory.DefaultResetUrl + "?token=", mail.TextBody);
        var row = Assert.Single(await TokensAsync());
        Assert.Equal(EmailTokens.HashOf(TokenIn(mail)), row.TokenHash);
        Assert.Equal(MailKind.PasswordReset, row.Kind);
        Assert.Equal(StorableTime.Now(Clock) + EmailTokens.ResetLifetime, row.ExpiresAt);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Request_for_an_address_without_an_account_is_dropped_without_a_mail()   // criterion 2
    {
        await EnqueueAsync(MailKind.PasswordReset, "nobody@example.com");

        Assert.Equal(1, await DispatchAsync());

        Assert.Empty(Mail.Sent);
        Assert.Empty(await TokensAsync());
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Verification_mail_goes_to_an_unconfirmed_account_only()   // criterion 11
    {
        await CreateUserAsync("new@example.com", confirmed: false);
        await EnqueueAsync(MailKind.EmailVerification, Factory.SeedEmail);   // confirmed
        await EnqueueAsync(MailKind.EmailVerification, "new@example.com");
        await EnqueueAsync(MailKind.EmailVerification, "nobody@example.com");

        Assert.Equal(3, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal("new@example.com", mail.To);
        Assert.Contains(AuthAppFactory.DefaultVerifyUrl + "?token=", mail.TextBody);
        Assert.Equal(MailKind.EmailVerification, Assert.Single(await TokensAsync()).Kind);
    }

    [Fact]
    public async Task Reset_mail_goes_to_an_unconfirmed_account_too()
    {
        await CreateUserAsync("new@example.com", confirmed: false);
        await EnqueueAsync(MailKind.PasswordReset, "new@example.com");

        await DispatchAsync();

        Assert.Equal("new@example.com", Assert.Single(Mail.Sent).To);
    }

    [Fact]
    public async Task Recipient_is_the_stored_address_not_the_submitted_spelling()
    {
        await CreateUserAsync("Anna.Nowak@Example.com", confirmed: true);
        await EnqueueAsync(MailKind.PasswordReset, "ANNA.NOWAK@example.COM");

        await DispatchAsync();

        Assert.Equal("Anna.Nowak@Example.com", Assert.Single(Mail.Sent).To);
    }

    [Fact]
    public async Task Newer_mail_replaces_the_earlier_token()   // criterion 6
    {
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();
        Clock.Advance(PastTheLimit);
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();

        Assert.Equal(2, Mail.Sent.Count);
        Assert.NotEqual(TokenIn(Mail.Sent[0]), TokenIn(Mail.Sent[1]));
        Assert.Equal(EmailTokens.HashOf(TokenIn(Mail.Sent[1])), Assert.Single(await TokensAsync()).TokenHash);
    }

    [Fact]
    public async Task Failed_send_is_retried_on_the_schedule_and_sent_once()   // criterion 14, Review Focus 4
    {
        Mail.Failing = true;
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);

        TimeSpan[] delays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)];
        for (var i = 0; i < delays.Length; i++)
        {
            Assert.Equal(1, await DispatchAsync());
            var row = Assert.Single(await QueueAsync());
            Assert.Equal(i + 1, row.Attempts);
            Assert.Equal(StorableTime.Now(Clock) + delays[i], row.NextAttemptAt);

            Assert.Equal(0, await DispatchAsync());                    // not due yet
            Clock.Advance(delays[i] - TimeSpan.FromSeconds(1));
            Assert.Equal(0, await DispatchAsync());                    // still not due
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Empty(Mail.Sent);
        Assert.Empty(await TokensAsync());                             // a failed attempt leaves no token behind
        // Every attempt composed a mail of its own, with a token of its own.
        Assert.Equal(delays.Length, Mail.Attempted.Select(TokenIn).Distinct().Count());

        Mail.Failing = false;
        Assert.Equal(1, await DispatchAsync());
        Assert.Equal(0, await DispatchAsync());

        Assert.Single(Mail.Sent);
        Assert.Equal(delays.Length + 1, Mail.Attempts);
        Assert.Empty(await QueueAsync());
        Assert.Single(await TokensAsync());
    }

    [Fact]
    public async Task Requests_for_addresses_without_an_account_do_not_hold_up_a_real_mail()
    {
        // Anyone can queue these. One statement clears them, however many there are, before a mail is composed.
        for (var i = 0; i < 40; i++)
        {
            await EnqueueAsync(MailKind.PasswordReset, $"nobody-{i}@example.com");
        }

        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);

        Assert.Equal(41, await DispatchAsync());

        Assert.Equal(Factory.SeedEmail, Assert.Single(Mail.Sent).To);
        Assert.Empty(await QueueAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_dispatchers_at_once_try_every_request_exactly_once(bool failing)
    {
        string[] addresses = ["a@example.com", "b@example.com", "c@example.com", "d@example.com"];
        foreach (var address in addresses)
        {
            await CreateUserAsync(address, confirmed: true);
            await EnqueueAsync(MailKind.PasswordReset, address);
        }

        // Slow enough that the two passes overlap on every row.
        Mail.Delay = TimeSpan.FromMilliseconds(100);
        Mail.Failing = failing;

        var handled = await Task.WhenAll(Task.Run(DispatchAsync), Task.Run(DispatchAsync));

        Assert.Equal(addresses.Length, handled.Sum());
        Assert.Equal(addresses.Order(), Mail.Attempted.Select(m => m.To).Order());
        Assert.Equal(failing ? 0 : addresses.Length, Mail.Sent.Count);
        var left = await QueueAsync();
        Assert.Equal(failing ? addresses.Length : 0, left.Count);
        Assert.All(left, row => Assert.Equal(1, row.Attempts));
    }

    [Fact]
    public async Task Next_attempt_is_timed_from_the_failure_not_from_the_start()
    {
        Mail.Failing = true;
        Mail.DuringSend = () =>
        {
            Clock.Advance(TimeSpan.FromSeconds(20));
            return Task.CompletedTask;
        };
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        var start = StorableTime.Now(Clock);

        Assert.Equal(1, await DispatchAsync());   // the row is not taken a second time in the same pass

        var row = Assert.Single(await QueueAsync());
        Assert.Equal(1, row.Attempts);
        Assert.Equal(start + TimeSpan.FromSeconds(20) + TimeSpan.FromSeconds(5), row.NextAttemptAt);
    }

    [Fact]
    public async Task Failed_send_leaves_the_earlier_link_in_force()   // Review Focus 4
    {
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();
        var earlier = EmailTokens.HashOf(TokenIn(Assert.Single(Mail.Sent)));

        Clock.Advance(PastTheLimit);
        Mail.Failing = true;
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();

        Assert.Equal(earlier, Assert.Single(await TokensAsync()).TokenHash);
        Assert.Equal(1, Assert.Single(await QueueAsync()).Attempts);
    }

    [Fact]
    public async Task Request_that_could_not_be_delivered_within_an_hour_is_dropped_and_logged()   // criterion 14
    {
        Mail.Failing = true;
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();

        Clock.Advance(MailDelivery.GiveUpAfter);
        Mail.Failing = false;
        Assert.Equal(1, await DispatchAsync());

        Assert.Empty(Mail.Sent);
        Assert.Empty(await QueueAsync());
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Error && e.Category.EndsWith(nameof(MailDispatcher), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_just_under_an_hour_old_is_still_sent()
    {
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);

        Clock.Advance(MailDelivery.GiveUpAfter - TimeSpan.FromSeconds(1));
        await DispatchAsync();

        Assert.Single(Mail.Sent);
    }

    [Fact]
    public async Task Request_recorded_before_a_restart_is_sent_after_it()   // criterion 14
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        await using (var before = new AuthAppFactory(Postgres, Keys, database).WithClock(Clock).WithoutHostedService<MailDispatchService>())
        {
            // Reading Services starts the host, which migrates the database and seeds the user.
            var decision = await before.Services.GetRequiredService<MailRequestStore>()
                .SubmitAsync(MailKind.PasswordReset, before.SeedEmail.ToUpperInvariant(), TestContext.Current.CancellationToken);
            Assert.True(decision.Allowed);
        }

        var mail = new CapturingMailTransport();
        await using var after = new AuthAppFactory(Postgres, Keys, database).WithClock(Clock)
            .WithoutHostedService<MailDispatchService>()
            .WithServices(services => services.Replace(ServiceDescriptor.Singleton<IMailTransport>(mail)));

        Assert.Equal(1, await after.Services.GetRequiredService<MailDispatcher>().DispatchDueAsync(TestContext.Current.CancellationToken));

        Assert.Equal(after.SeedEmail, Assert.Single(mail.Sent).To);
    }

    [Fact]
    public async Task No_token_and_no_mail_body_reach_the_log()   // criterion 15
    {
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();
        var token = TokenIn(Assert.Single(Mail.Sent));

        Clock.Advance(PastTheLimit);
        Mail.Failing = true;
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();                                         // a failed send is logged

        Assert.Contains(Logs.Entries, e => e.Category.EndsWith(nameof(MailDispatcher), StringComparison.Ordinal));
        Assert.DoesNotContain(token, Logs.Text);
        Assert.DoesNotContain("token=", Logs.Text);
        Assert.DoesNotContain(Factory.SeedEmail, Logs.Text.Replace("Seeded development user " + Factory.SeedEmail, "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }
}
