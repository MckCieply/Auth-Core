using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class InvitePruningTests : TenancyTestBase
{
    public InvitePruningTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // Left out of the host so that the pass a test counts is its own; one test runs the service itself, in another class.
        Factory.WithoutHostedService<EmailPruningService>();
    }

    private Task<int> PruneAsync() =>
        Factory.Services.GetRequiredService<EmailPruner>().PruneInvitesAsync(TestContext.Current.CancellationToken);

    private Task<int> InviteCountAsync() => InDbAsync(db => db.Invites.CountAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Pruning_removes_expired_invitations_and_keeps_the_others()   // spec 0005 → Invitations
    {
        var company = await CreateCompanyAsync("Acme");
        var old = await AddInviteAsync(company, "old@acme.test", "user");
        Clock.Advance(TimeSpan.FromDays(1));
        var newer = await AddInviteAsync(company, "newer@acme.test", "user");
        Clock.Advance(InviteTokens.Lifetime - TimeSpan.FromDays(1));   // the first is exactly seven days old

        Assert.Equal(1, await PruneAsync());

        Assert.Null(await InviteAsync(old));
        Assert.NotNull(await InviteAsync(newer));
        Assert.Equal(0, await PruneAsync());
        Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, await PruneAsync());
        Assert.Equal(0, await InviteCountAsync());
    }

    [Fact]
    public async Task A_mailed_invitation_lives_seven_days_from_its_mail_not_from_its_making()
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "a@acme.test", "user");
        Clock.Advance(TimeSpan.FromDays(3));
        await EnqueueInvitationAsync(invite, "a@acme.test");
        await DispatchAsync();

        Clock.Advance(TimeSpan.FromDays(6));   // nine days after the making, six after the mail

        Assert.Equal(0, await PruneAsync());
        Assert.NotNull(await InviteAsync(invite));
    }

    [Fact]
    public async Task Pruning_leaves_the_queue_and_the_mail_limits_alone()
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "a@acme.test", "user");
        await EnqueueInvitationAsync(invite, "a@acme.test");
        Clock.Advance(InviteTokens.Lifetime);

        await PruneAsync();

        Assert.Equal(1, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
    }
}

public sealed class InvitePruningServiceTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    [Fact]
    public async Task The_hourly_pass_of_the_service_removes_expired_invitations()   // spec 0005: removed within a day
    {
        Assert.True(EmailPruningService.Interval <= TimeSpan.FromDays(1));
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "a@acme.test", "user");

        Clock.Advance(InviteTokens.Lifetime + EmailPruningService.Interval);   // the timer of the service fires on the fake clock

        await Poll.UntilAsync(() => InviteAsync(invite).GetAwaiter().GetResult() is null, "the pruning service to remove the invitation");
    }
}
