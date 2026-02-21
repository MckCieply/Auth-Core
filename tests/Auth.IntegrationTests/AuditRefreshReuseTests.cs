using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Seeding;

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
