using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;

namespace Auth.IntegrationTests;

public sealed class TokenPruningTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    // The host's own pruning loop waits on the fake clock, and Clock.Advance fires that timer: a background pass would
    // race the explicit pass of a test and delete what the test expects to count. Stop the loop before the clock
    // moves, so a test drives exactly one pass.
    private async Task StopBackgroundPruningAsync()
    {
        var service = Factory.Services.GetServices<IHostedService>().OfType<TokenPruningService>().Single();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(long Tokens, long Authorizations)> PruneAsync() =>
        await Factory.Services.GetRequiredService<TokenPruner>().PruneOnceAsync(TestContext.Current.CancellationToken);

    private async Task<bool> EntryExistsAsync(string reference)
    {
        using var scope = Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        return await tokens.FindByReferenceIdAsync(reference) is not null;
    }

    [Fact]
    public async Task Pruning_removes_an_ended_session_older_than_the_sliding_window()
    {
        await StopBackgroundPruningAsync();

        var login = await SessionApi.LoginAsync(Client, Factory);
        using var logout = await SessionApi.Logout(Client, login.RefreshToken);

        Clock.Advance(TimeSpan.FromDays(15));
        var (tokens, authorizations) = await PruneAsync();

        Assert.True(tokens >= 2, $"Expected the access and refresh token entries to go, pruned {tokens}.");
        Assert.Equal(1, authorizations);
        Assert.False(await EntryExistsAsync(login.RefreshToken));
    }

    [Fact]
    public async Task Pruning_removes_consumed_entries_older_than_the_sliding_window()
    {
        await StopBackgroundPruningAsync();

        var login = await SessionApi.LoginAsync(Client, Factory);
        Clock.Advance(TimeSpan.FromDays(13));
        var day13 = await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(TimeSpan.FromDays(2));          // day 15: the login token is consumed and 15 days old
        await PruneAsync();

        Assert.False(await EntryExistsAsync(login.RefreshToken));
        await SessionApi.RefreshOk(Client, day13.RefreshToken);   // the live session is untouched
    }

    [Fact]
    public async Task Pruning_keeps_a_consumed_entry_that_reuse_detection_still_needs()   // Decision 13
    {
        await StopBackgroundPruningAsync();

        var login = await SessionApi.LoginAsync(Client, Factory);
        var current = await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(TimeSpan.FromDays(13));
        await PruneAsync();

        // Had the consumed entry been pruned, the replay would be a plain unknown token and `current` would live on.
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        using var afterReplay = await SessionApi.Refresh(Client, current.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(replay);
        await SessionApi.AssertInvalidGrantAsync(afterReplay);
    }

    [Fact]
    public async Task Pruning_leaves_a_live_session_working()
    {
        await StopBackgroundPruningAsync();

        var login = await SessionApi.LoginAsync(Client, Factory);

        Clock.Advance(TimeSpan.FromDays(1));
        await PruneAsync();

        await SessionApi.RefreshOk(Client, login.RefreshToken);
    }
}
