using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0015 → Locks, criterion 29: a switch (and a sign-in) against a removal of the membership it names, in parallel requests against
/// PostgreSQL. The test holds a lock, the request queues behind it, and the removal is committed before or after the request writes its session.
/// </summary>
public sealed class SwitchRaceTests : AuditTestBase
{
    private const string BAdmin = "boss@second.test";

    public SwitchRaceTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
    }

    private async Task<(Guid User, Guid A, Guid B)> SeedInTwoCompaniesAsync()
    {
        var user = await Factory.SeedUserIdAsync();
        var a = await DevCompanyIdAsync();
        var b = await CreateCompanyAsync("Second");
        await AddMembershipAsync(user, b, "user");
        return (user, a, b);
    }

    private static string OrgOf(SessionApi.Session session) => AccessTokens.Text(session.AccessToken, "org_id");

    private Task<HttpResponseMessage> RemoveAsync(string adminToken, Guid user) =>
        TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{user}", adminToken);

    [Fact]
    public async Task A_removal_committed_before_a_switch_makes_it_404_and_the_cookie_still_refreshes()
    {
        var (user, a, b) = await SeedInTwoCompaniesAsync();
        var login = await SessionApi.LoginAsync(Client, Factory);

        Task<HttpResponseMessage> pending;
        await using (var held = await HeldLock.MembershipRowAsync(Factory, user, b))
        {
            pending = SessionApi.SwitchTo(Client, login.RefreshToken, b);
            await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
            await held.LockAccountAsync(user);   // the switch holds no account row while it waits for the membership
            Assert.Equal(1, await held.DeleteMembershipAsync(user, b));
            await held.ReleaseAsync();
        }

        using var response = await pending;
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("""{"error":"not_found"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("valid", await StoredTokens.StatusAsync(Factory, login.RefreshToken));
        Assert.Equal(a.ToString(), OrgOf(await SessionApi.RefreshOk(Client, login.RefreshToken)));
    }

    [Fact]
    public async Task A_removal_of_the_session_company_committed_before_the_switch_makes_it_401()
    {
        var (user, a, b) = await SeedInTwoCompaniesAsync();
        var login = await SessionApi.LoginAsync(Client, Factory);

        Task<HttpResponseMessage> pending;
        await using (var held = await HeldLock.MembershipRowAsync(Factory, user, a))
        {
            pending = SessionApi.SwitchTo(Client, login.RefreshToken, b);
            await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
            await held.LockAccountAsync(user);
            Assert.Equal(1, await held.DeleteMembershipAsync(user, a));
            await held.ReleaseAsync();
        }

        using var response = await pending;
        await SessionApi.AssertInvalidGrantAsync(response);
        Assert.Null(await LastUsedAtAsync(user, b));
    }

    [Fact]
    public async Task A_removal_of_the_session_company_in_a_switch_to_it_makes_it_401_and_writes_nothing()   // the same company: whenSpaceGone is the 401, not the 404
    {
        var (user, a, _) = await SeedInTwoCompaniesAsync();
        var login = await SessionApi.LoginAsync(Client, Factory);

        Task<HttpResponseMessage> pending;
        await using (var held = await HeldLock.MembershipRowAsync(Factory, user, a))
        {
            pending = SessionApi.SwitchTo(Client, login.RefreshToken, a);
            await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
            await held.LockAccountAsync(user);
            Assert.Equal(1, await held.DeleteMembershipAsync(user, a));
            await held.ReleaseAsync();
        }

        using var response = await pending;
        await SessionApi.AssertInvalidGrantAsync(response);
        Assert.Equal("valid", await StoredTokens.StatusAsync(Factory, login.RefreshToken));
    }

    [Fact]
    public async Task A_session_company_made_again_in_a_switch_to_it_makes_it_401_and_its_last_used_at_stays()
    {
        var (user, a, _) = await SeedInTwoCompaniesAsync();
        var login = await SessionApi.LoginAsync(Client, Factory);
        var before = await LastUsedAtAsync(user, a);
        Assert.NotNull(before);

        Task<HttpResponseMessage> pending;
        await using (var held = await HeldLock.MembershipRowAsync(Factory, user, a))
        {
            pending = SessionApi.SwitchTo(Client, login.RefreshToken, a);
            await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
            await held.LockAccountAsync(user);
            Assert.Equal(1, await held.RemakeMembershipAsync(user, a));   // the row is there, but it is not the one the session names
            await held.ReleaseAsync();
        }

        using var response = await pending;
        await SessionApi.AssertInvalidGrantAsync(response);
        Assert.Equal(before, await LastUsedAtAsync(user, a));
    }

    [Fact]
    public async Task A_removal_committed_after_a_switch_is_caught_at_the_next_refresh()
    {
        var (user, _, b) = await SeedInTwoCompaniesAsync();
        await AddMemberAsync(b, BAdmin, "admin");
        var adminToken = (await SessionApi.LoginAsync(Client, BAdmin, UserPassword)).AccessToken;
        var login = await SessionApi.LoginAsync(Client, Factory);

        Task<HttpResponseMessage> switching;
        Task<HttpResponseMessage> removal;
        await using (var audit = await HeldLock.AuditTableAsync(Factory))
        {
            switching = SessionApi.SwitchTo(Client, login.RefreshToken, b);
            await HeldLock.WaitForWaitersAsync(Factory, 1, switching);   // the switch holds B's row and waits at its org.switched insert
            removal = RemoveAsync(adminToken, user);
            await HeldLock.WaitForWaitersAsync(Factory, 2, switching, removal);   // the removal waits for B's row
            Assert.Equal(1, await HeldLock.RowLockWaitersAsync(Factory));   // and it is the row it waits for, not only the audit table
            await audit.ReleaseAsync();
        }

        using var switched = await switching;
        using var removed = await removal;
        var session = await SessionApi.ReadSessionAsync(switched);
        Assert.Equal(b.ToString(), OrgOf(session));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var next = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(next);
    }

    [Fact]
    public async Task A_removal_committed_after_a_login_is_caught_at_the_next_refresh()
    {
        var (user, _, b) = await SeedInTwoCompaniesAsync();
        await AddMemberAsync(b, BAdmin, "admin");
        var adminToken = (await SessionApi.LoginAsync(Client, BAdmin, UserPassword)).AccessToken;
        await InDbAsync(db => db.Memberships.Where(m => m.UserId == user && m.CompanyId == b)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedAt, StorableTime.Now(Clock).AddHours(1)), TestContext.Current.CancellationToken));   // the login lands in B

        Task<HttpResponseMessage> login;
        Task<HttpResponseMessage> removal;
        await using (var audit = await HeldLock.AuditTableAsync(Factory))
        {
            login = LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);
            await HeldLock.WaitForWaitersAsync(Factory, 1, login);   // the login holds B's row and waits at its login.succeeded insert
            removal = RemoveAsync(adminToken, user);
            await HeldLock.WaitForWaitersAsync(Factory, 2, login, removal);
            Assert.Equal(1, await HeldLock.RowLockWaitersAsync(Factory));   // the removal waits for B's row, not only for the audit table
            await audit.ReleaseAsync();
        }

        using var loggedIn = await login;
        using var removed = await removal;
        var session = await SessionApi.ReadSessionAsync(loggedIn);
        Assert.Equal(b.ToString(), OrgOf(session));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var next = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(next);
    }
}
