using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Auth.IntegrationTests;

public sealed class AuditPruningTests : SessionTestBase
{
    public AuditPruningTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The hosts' pruning loops wait on the fake clock: a background pass would race the explicit pass a test counts.
        Factory.WithoutHostedService<AuditPruningService>();
    }

    private async Task AddRowAsync(string email, TimeSpan age)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OccurredAt = Clock.GetUtcNow() - age,
            Kind = AuditKinds.LoginFailed,
            SubjectEmail = email,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<string?>> EmailsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().AuditEvents.AsNoTracking()
            .OrderBy(a => a.SubjectEmail).Select(a => a.SubjectEmail).ToListAsync(TestContext.Current.CancellationToken);
    }

    private Task<int> PruneAsync() => Factory.Services.GetRequiredService<AuditPruner>().PruneOnceAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Rows_older_than_the_retention_are_pruned_and_newer_ones_stay()   // criterion 9
    {
        await AddRowAsync("ancient", TimeSpan.FromDays(400));
        await AddRowAsync("old", TimeSpan.FromDays(91));
        await AddRowAsync("edge", TimeSpan.FromDays(90) - TimeSpan.FromMinutes(1));
        await AddRowAsync("young", TimeSpan.FromDays(89));
        await AddRowAsync("new", TimeSpan.Zero);

        Assert.Equal(2, await PruneAsync());

        Assert.Equal(["edge", "new", "young"], await EmailsAsync());
    }

    [Fact]
    public async Task The_clock_decides_what_is_old()
    {
        await AddRowAsync("a", TimeSpan.FromDays(10));

        Assert.Equal(0, await PruneAsync());

        Clock.Advance(TimeSpan.FromDays(81));   // now 91 days old

        Assert.Equal(1, await PruneAsync());
        Assert.Empty(await EmailsAsync());
    }

    [Fact]
    public async Task A_pass_with_nothing_to_remove_removes_nothing()
    {
        await AddRowAsync("a", TimeSpan.FromDays(1));

        Assert.Equal(0, await PruneAsync());
        Assert.Equal(["a"], await EmailsAsync());
    }

    [Fact]
    public async Task The_retention_is_the_setting()   // criterion 9
    {
        await using var factory = new AuthAppFactory(Postgres, Keys)
            .WithClock(Clock)
            .WithSetting(AuditSettings.RetentionDaysKey, "7")
            .WithoutHostedService<AuditPruningService>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), OccurredAt = Clock.GetUtcNow() - TimeSpan.FromDays(8), Kind = AuditKinds.LoginFailed, SubjectEmail = "eight" });
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), OccurredAt = Clock.GetUtcNow() - TimeSpan.FromDays(6), Kind = AuditKinds.LoginFailed, SubjectEmail = "six" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var removed = await factory.Services.GetRequiredService<AuditPruner>().PruneOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task The_service_is_part_of_the_host_and_runs_every_hour()
    {
        await using var factory = new AuthAppFactory(Postgres, Keys).WithClock(Clock);
        _ = factory.Services;
        Assert.Single(factory.Services.GetServices<IHostedService>().OfType<AuditPruningService>());
        Assert.Equal(TimeSpan.FromHours(1), AuditPruningService.Interval);
    }
}
