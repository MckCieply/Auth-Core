using System.Security.Cryptography;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class LoginStreakStoreTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private LoginStreakStore Store => Factory.Services.GetRequiredService<LoginStreakStore>();

    private static byte[] Key(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private async Task<LoginStreak?> RowAsync(byte[] key)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.LoginStreaks.AsNoTracking().SingleOrDefaultAsync(s => s.IdentifierHash == key, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task First_attempt_creates_the_row_and_is_allowed()
    {
        var key = Key("first");

        var decision = await Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken);

        Assert.True(decision.Allowed);
        var row = await RowAsync(key);
        Assert.NotNull(row);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal(32, row.IdentifierHash.Length);
    }

    [Fact]
    public async Task Parallel_attempts_on_a_new_identifier_are_each_counted_once()   // Review Focus 1, Decision 14
    {
        var key = Key("race");

        // Twelve, not more: every waiting caller holds a server connection, and the PostgreSQL container's 100 are
        // shared with every test class running in parallel.
        const int Callers = 12;
        var decisions = await Task.WhenAll(Enumerable.Range(0, Callers).Select(_ =>
            Task.Run(() => Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken))));

        // The clock is frozen, so no cooldown expires meanwhile: once the streak locks, nothing else gets through.
        var allowed = decisions.Count(d => d.Allowed);
        Assert.InRange(allowed, 1, LockoutPolicy.Threshold);
        // No attempt is lost. Written so that it also holds once a rule raises the streak to the threshold early
        // (Task 3): reaching the lock after `allowed` attempts skips `Threshold - allowed` numbers.
        Assert.Equal(Callers + LockoutPolicy.Threshold - allowed, (await RowAsync(key))!.AttemptCount);
    }

    [Fact]
    public async Task Stored_instants_read_back_equal()
    {
        // PostgreSQL keeps microseconds (10 ticks): make sure the clock sits between two of them, or this test proves nothing.
        Clock.Advance(TimeSpan.FromTicks(17 - (Clock.GetUtcNow().Ticks % 10)));
        var key = Key("precision");
        for (var i = 0; i < LockoutPolicy.Threshold; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(3));
            await Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken);
        }

        var refused = await Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken);

        // Exactly two minutes: PostgreSQL keeps microseconds, and the store must compute on what the row can hold.
        Assert.False(refused.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(2), refused.RetryAfter);
    }

    [Fact]
    public async Task Identifiers_do_not_share_a_streak()
    {
        await Store.RegisterAttemptAsync(Key("one"), TestContext.Current.CancellationToken);
        await Store.RegisterAttemptAsync(Key("one"), TestContext.Current.CancellationToken);
        await Store.RegisterAttemptAsync(Key("two"), TestContext.Current.CancellationToken);

        Assert.Equal(2, (await RowAsync(Key("one")))!.AttemptCount);
        Assert.Equal(1, (await RowAsync(Key("two")))!.AttemptCount);
    }

    [Fact]
    public async Task Clear_removes_the_streak_and_the_next_attempt_is_the_first()
    {
        var key = Key("clear");
        await Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken);
        await Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken);

        await Store.ClearAsync(key, TestContext.Current.CancellationToken);
        Assert.Null(await RowAsync(key));

        await Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken);
        Assert.Equal(1, (await RowAsync(key))!.AttemptCount);
    }

    [Fact]
    public async Task Clear_of_an_unknown_identifier_does_nothing() =>
        await Store.ClearAsync(Key("never-seen"), TestContext.Current.CancellationToken);
}
