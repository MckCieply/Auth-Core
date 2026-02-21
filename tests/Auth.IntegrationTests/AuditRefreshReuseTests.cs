using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Seeding;
using Auth.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

public sealed class AuditRefreshReuseTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    [Fact]
    public async Task A_consumed_token_presented_after_the_leeway_is_recorded_once_with_its_account()   // criterion 8
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(replay);

        var row = await SingleAsync(AuditKinds.RefreshReuseDetected);
        var seed = await Factory.SeedUserIdAsync();
        Assert.Null(row.ActorUserId);   // whoever replayed the token is not known
        Assert.Equal(seed, row.SubjectUserId);
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal(DevUserSeeder.DefaultOrgName, row.OrgName);
        Assert.Equal("unknown", row.ClientIp);   // the helper sends no address

        using var again = await SessionApi.Refresh(Client, login.RefreshToken);   // the family is revoked now: nothing more to detect
        await SessionApi.AssertInvalidGrantAsync(again);
        Assert.Single(await AuditAsync(AuditKinds.RefreshReuseDetected));
        await AssertNoSecretsAsync(login.RefreshToken);
    }

    [Fact]
    public async Task A_consumed_token_presented_inside_the_leeway_is_an_honest_retry_and_writes_nothing()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(TimeSpan.FromSeconds(10));
        _ = await SessionApi.RefreshOk(Client, login.RefreshToken);

        Assert.Empty(await AuditAsync(AuditKinds.RefreshReuseDetected));
    }

    [Fact]
    public async Task Tokens_that_are_unknown_or_revoked_or_current_write_nothing()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        using (var unknown = await SessionApi.Refresh(Client, "a-cookie-nobody-issued"))
        {
            await SessionApi.AssertInvalidGrantAsync(unknown);
        }

        var current = await SessionApi.RefreshOk(Client, login.RefreshToken);
        _ = await SessionApi.RefreshOk(Client, current.RefreshToken);
        using (var logout = await SessionApi.Logout(Client, current.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        using (var revoked = await SessionApi.Refresh(Client, current.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(revoked);
        }

        Assert.Empty(await AuditAsync(AuditKinds.RefreshReuseDetected));
    }

    [Fact]
    public async Task A_replay_exactly_at_the_leeway_is_rejected_and_recorded()   // the boundary is OpenIddict's, and ours is the same
    {
        var (rejected, rows) = await ReplayAfterAsync(SessionPolicy.ReuseLeeway);

        Assert.True(rejected);
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task A_replay_a_millisecond_inside_the_leeway_is_let_through_and_not_recorded()
    {
        var (rejected, rows) = await ReplayAfterAsync(SessionPolicy.ReuseLeeway - TimeSpan.FromMilliseconds(1));

        Assert.False(rejected);
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task A_row_exists_exactly_when_the_replay_is_answered_invalid_grant()
    {
        foreach (var delay in new[] { TimeSpan.FromMilliseconds(14_999), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(15_001) })
        {
            var (rejected, rows) = await ReplayAfterAsync(delay);
            Assert.Equal(rejected ? 1 : 0, rows);
            await InDbAsync(db => db.AuditEvents.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        }
    }

    private async Task<(bool Rejected, int Rows)> ReplayAfterAsync(TimeSpan delay)
    {
        // The clock starts at the real time, with sub-microsecond ticks, and PostgreSQL stores the redemption date to the microsecond:
        // from a whole second on, "exactly the leeway" is exact.
        Clock.SetUtcNow(new DateTimeOffset((Clock.GetUtcNow().UtcTicks / TimeSpan.TicksPerSecond + 1) * TimeSpan.TicksPerSecond, TimeSpan.Zero));
        var login = await SessionApi.LoginAsync(Client, Factory);
        await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(delay);
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        var rejected = replay.StatusCode == HttpStatusCode.Unauthorized;
        if (rejected)
        {
            await SessionApi.AssertInvalidGrantAsync(replay);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        return (rejected, (await AuditAsync(AuditKinds.RefreshReuseDetected)).Count);
    }

    [Fact]
    public async Task A_failing_lookup_of_the_company_does_not_change_the_answer_or_stop_the_revocation()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        var next = await SessionApi.RefreshOk(Client, login.RefreshToken);
        Clock.Advance(TimeSpan.FromSeconds(20));
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("""ALTER TABLE "Memberships" RENAME TO "Memberships_away" """, TestContext.Current.CancellationToken));

        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        using var afterReplay = await SessionApi.Refresh(Client, next.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(replay);        // not a 500: OpenIddict still decides the reuse
        await SessionApi.AssertInvalidGrantAsync(afterReplay);   // and still revoked the family
        Assert.Empty(await AuditAsync(AuditKinds.RefreshReuseDetected));   // nothing was written for it
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Warning && e.Category.EndsWith("RefreshReuseAuditHandler", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_handler_does_not_change_what_the_refresh_answers()   // spec 0002 stays as it is
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        var next = await SessionApi.RefreshOk(Client, login.RefreshToken);
        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        using var afterReplay = await SessionApi.Refresh(Client, next.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(replay);
        await SessionApi.AssertInvalidGrantAsync(afterReplay);   // the family died with the replay, as before
    }
}
