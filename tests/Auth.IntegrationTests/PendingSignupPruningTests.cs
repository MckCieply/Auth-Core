using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Signup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class PendingSignupPruningTests : MailTestBase
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    public PendingSignupPruningTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The loop of the service waits on the fake clock and Clock.Advance fires its timer: a background pass would race the explicit pass.
        Factory.WithoutHostedService<EmailPruningService>();
    }

    private Task<int> PruneAsync() =>
        Factory.Services.GetRequiredService<EmailPruner>().PrunePendingSignupsAsync(TestContext.Current.CancellationToken);

    private Task<int> CountAsync() => InDbAsync(db => db.PendingSignups.CountAsync(TestContext.Current.CancellationToken));

    private Task<int> AddAsync(string normalized) =>
        InDbAsync(async db =>
        {
            await PendingSignups.UpsertAsync(db, normalized, normalized.ToLowerInvariant(), "Firma", StorableTime.Now(Clock), TestContext.Current.CancellationToken);
            return 0;
        });

    [Fact]
    public async Task Pruning_removes_pending_sign_ups_whose_time_has_passed_and_keeps_the_others()   // criterion 13
    {
        await AddAsync("OLD@FIRMA.TEST");
        Clock.Advance(TimeSpan.FromHours(1));
        await AddAsync("NEWER@FIRMA.TEST");
        Clock.Advance(Day - TimeSpan.FromHours(1));   // the first is exactly 24 hours old

        Assert.Equal(1, await PruneAsync());

        var left = await InDbAsync(db => db.PendingSignups.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal("NEWER@FIRMA.TEST", left.NormalizedEmail);
        Assert.Equal(0, await PruneAsync());
        Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await PruneAsync());
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task A_row_with_a_live_link_survives_a_pass()
    {
        await AddAsync("ANNA@FIRMA.TEST");
        Clock.Advance(TimeSpan.FromHours(20));
        await InDbAsync(db => PendingSignups.IssueAsync(db, "ANNA@FIRMA.TEST", StorableTime.Now(Clock), TestContext.Current.CancellationToken));   // the mail was sent at 20 hours
        Clock.Advance(TimeSpan.FromHours(10));   // 30 hours after the request, 10 after the mail

        Assert.Equal(0, await PruneAsync());
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task Pruning_leaves_the_queue_the_limits_and_the_accounts_alone()
    {
        await AddAsync("ANNA@FIRMA.TEST");
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            db.MailRequests.Add(new MailRequest { Kind = MailKind.Signup, NormalizedEmail = "ANNA@FIRMA.TEST", RequestedAt = now, NextAttemptAt = now });
            await MailLimits.RegisterAsync(db, LoginIdentifier.HashOf("ANNA@FIRMA.TEST"), MailKind.Signup, now, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });
        Clock.Advance(Day);

        Assert.Equal(1, await PruneAsync());

        Assert.Equal(1, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.MailRequestLimits.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Factory.SeedEmail, TestContext.Current.CancellationToken)));
    }
}

public sealed class PendingSignupPruningServiceTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    [Fact]
    public async Task The_hourly_pass_of_the_service_removes_expired_pending_sign_ups()   // criterion 13
    {
        await InDbAsync(async db =>
        {
            await PendingSignups.UpsertAsync(db, "ANNA@FIRMA.TEST", "anna@firma.test", "Firma", StorableTime.Now(Clock), TestContext.Current.CancellationToken);
            return 0;
        });

        Clock.Advance(TimeSpan.FromHours(24) + EmailPruningService.Interval);   // the timer of the service fires on the fake clock

        await Poll.UntilAsync(
            () => InDbAsync(db => db.PendingSignups.CountAsync(TestContext.Current.CancellationToken)).GetAwaiter().GetResult() == 0,
            "the pruning service to remove the pending sign-up");
    }
}
