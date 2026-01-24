using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Auth.IntegrationTests;

public sealed class LockoutPruningTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    // The host's own pruning loop waits on the fake clock, and Clock.Advance fires its timer: a background pass
    // would race the explicit pass of a test. Stop the loop before the clock moves.
    private async Task StopBackgroundPruningAsync()
    {
        var service = Factory.Services.GetServices<IHostedService>().OfType<LockoutPruningService>().Single();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> PruneAsync() =>
        await Factory.Services.GetRequiredService<LockoutPruner>().PruneOnceAsync(TestContext.Current.CancellationToken);

    private async Task<int> RowCountAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().LoginStreaks.CountAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pruning_removes_streaks_without_an_attempt_for_24_hours()
    {
        await StopBackgroundPruningAsync();
        await LockoutApi.FailAsync(Client, Clock, "ghost-1@example.com", 2);
        await LockoutApi.FailAsync(Client, Clock, "ghost-2@example.com", 10);   // locked, then long expired

        Clock.Advance(LockoutPolicy.StreakLifetime);

        Assert.Equal(2, await PruneAsync());
        Assert.Equal(0, await RowCountAsync());
    }

    [Fact]
    public async Task Pruning_keeps_a_younger_streak_and_it_still_counts()
    {
        await StopBackgroundPruningAsync();
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);

        Clock.Advance(TimeSpan.FromHours(23));

        Assert.Equal(0, await PruneAsync());
        Assert.Equal(1, await RowCountAsync());

        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 1);   // the tenth: starts the cooldown
        using var refused = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);
        await LockoutApi.AssertLockedAsync(refused);
    }

    [Fact]
    public async Task Pruning_removes_only_the_old_rows()
    {
        await StopBackgroundPruningAsync();
        await LockoutApi.FailAsync(Client, Clock, "old@example.com", 1);
        Clock.Advance(LockoutPolicy.StreakLifetime - TimeSpan.FromMinutes(1));
        await LockoutApi.FailAsync(Client, Clock, "young@example.com", 1);
        Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, await PruneAsync());
        Assert.Equal(1, await RowCountAsync());
    }

    [Fact]
    public async Task Attempt_racing_a_pruning_pass_is_still_answered()
    {
        await StopBackgroundPruningAsync();
        await LockoutApi.FailAsync(Client, Clock, "ghost@example.com", 1);
        Clock.Advance(LockoutPolicy.StreakLifetime);

        // The pass deletes the row these attempts are about to count on. None of them may fail for it.
        var pass = Task.Run(PruneAsync);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => LoginApi.Login(Client, "ghost@example.com", LockoutApi.WrongPassword))));
        await pass;

        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Successful_logins_leave_nothing_to_prune()
    {
        await StopBackgroundPruningAsync();
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);

        Assert.Equal(0, await RowCountAsync());
    }
}
