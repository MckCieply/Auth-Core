using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class EmailPruningTests : MailTestBase
{
    public EmailPruningTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The service's loop waits on the fake clock and Clock.Advance fires its timer: a background pass would race
        // the explicit pass a test counts. It is left out, and a test drives its pass by hand.
        Factory.WithoutHostedService<EmailPruningService>();
    }

    private Task<(int Tokens, int Limits)> PruneAsync() =>
        Factory.Services.GetRequiredService<EmailPruner>().PruneOnceAsync(TestContext.Current.CancellationToken);

    private Task<int> TokenCountAsync() =>
        InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken));

    private Task<int> LimitCountAsync() =>
        InDbAsync(db => db.MailRequestLimits.CountAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Pruning_removes_expired_tokens_and_keeps_the_others()
    {
        await CreateUserAsync("new@example.com", confirmed: false);
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);            // expires after 1 hour
        await EnqueueAsync(MailKind.EmailVerification, "new@example.com");        // expires after 24 hours
        await DispatchAsync();
        Assert.Equal(2, await TokenCountAsync());

        Clock.Advance(EmailTokens.ResetLifetime);

        Assert.Equal(1, (await PruneAsync()).Tokens);
        var left = await InDbAsync(db => db.EmailTokens.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(MailKind.EmailVerification, left.Kind);

        Clock.Advance(EmailTokens.VerificationLifetime);
        Assert.Equal(1, (await PruneAsync()).Tokens);
        Assert.Equal(0, await TokenCountAsync());
    }

    [Fact]
    public async Task Pruning_removes_a_limit_row_an_hour_after_its_last_accepted_request()
    {
        await EnqueueAsync(MailKind.PasswordReset, "nobody@example.com");

        Clock.Advance(MailLimitPolicy.Window - TimeSpan.FromSeconds(1));
        Assert.Equal(0, (await PruneAsync()).Limits);
        Assert.Equal(1, await LimitCountAsync());

        Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await PruneAsync()).Limits);
        Assert.Equal(0, await LimitCountAsync());
    }

    [Fact]
    public async Task Limit_row_that_still_counts_survives_a_pass()   // criterion 9
    {
        for (var i = 0; i < 5; i++)
        {
            using var accepted = await AccountApi.Forgot(Client, Factory.SeedEmail);
            await AccountApi.AssertEmptyAsync(accepted, HttpStatusCode.Accepted);
            Clock.Advance(TimeSpan.FromSeconds(61));
        }

        Clock.Advance(TimeSpan.FromMinutes(50));          // 55 minutes into the window

        Assert.Equal(0, (await PruneAsync()).Limits);
        using var sixth = await AccountApi.Forgot(Client, Factory.SeedEmail);
        await LockoutApi.AssertLockedAsync(sixth);
    }

    [Fact]
    public async Task Pruned_address_starts_afresh()
    {
        using (var first = await AccountApi.Forgot(Client, Factory.SeedEmail))
        {
            await AccountApi.AssertEmptyAsync(first, HttpStatusCode.Accepted);
        }

        Clock.Advance(MailLimitPolicy.Window);
        await PruneAsync();

        using var again = await AccountApi.Forgot(Client, Factory.SeedEmail);
        await AccountApi.AssertEmptyAsync(again, HttpStatusCode.Accepted);
        Assert.Equal(1, await LimitCountAsync());
    }

    [Fact]
    public async Task Pruning_leaves_the_queue_alone()
    {
        Mail.Failing = true;
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();

        Clock.Advance(TimeSpan.FromHours(2));
        await PruneAsync();

        Assert.Equal(1, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
    }
}
