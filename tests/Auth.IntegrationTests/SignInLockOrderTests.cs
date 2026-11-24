using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0015 → Lock order: a sign-in locks the membership it signs in to before it locks the account row, so a path that holds a membership
/// row (a removal) and then the account row cannot deadlock with it. Each test holds the membership row, sends the request, waits until it is
/// queued behind it, and locks the account row in its own transaction: that must succeed, because the request holds no account lock while
/// it waits.
/// </summary>
public sealed class SignInLockOrderTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;

    private Task<int> SessionRowsAsync(Guid user)
    {
        var subject = user.ToString();
        return InDbAsync(async db =>
            await db.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync(t => t.Subject == subject, TestContext.Current.CancellationToken)
            + await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().CountAsync(a => a.Subject == subject, TestContext.Current.CancellationToken));
    }

    private Task<HttpResponseMessage> SeedLogin() => LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

    [Fact]
    public async Task A_removal_committed_before_a_login_makes_it_fail_without_a_deadlock()   // criterion 29
    {
        var user = await Factory.SeedUserIdAsync();
        var company = await DevCompanyIdAsync();
        Task<HttpResponseMessage> login;
        await using (var held = await HeldLock.MembershipRowAsync(Factory, user, company))
        {
            login = SeedLogin();
            await HeldLock.WaitForWaitersAsync(Factory, 1, login);

            await held.LockAccountAsync(user);   // the login holds no account lock while it waits for the membership
            await held.DeleteMembershipAsync(user, company);
            await held.DeleteAccountAsync(user);
            await held.ReleaseAsync();
        }

        using var response = await login;

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, await SessionRowsAsync(user));
    }

    [Fact]
    public async Task A_removal_of_the_last_membership_while_a_login_waits_commits_and_the_login_takes_nothing()
    {
        var user = await Factory.SeedUserIdAsync();
        var first = await DevCompanyIdAsync();
        var second = await CreateCompanyAsync("Second");
        await AddMembershipAsync(user, second, "user");
        Task<HttpResponseMessage> login;
        await using (var held = await HeldLock.MembershipRowAsync(Factory, user, first))   // the one the login chooses: it joined first
        {
            login = SeedLogin();
            await HeldLock.WaitForWaitersAsync(Factory, 1, login);

            await held.LockAccountAsync(user);
            await held.DeleteMembershipAsync(user, first);
            await held.ReleaseAsync();
        }

        using (var response = await login)
        {
            // As for an account gone: the path's own answer, nothing written.
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("""{"error":"invalid_credentials"}""", await response.Content.ReadAsStringAsync());
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        Assert.Equal(0, await SessionRowsAsync(user));
        Assert.Null(await LastUsedAtAsync(user, second));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));

        var again = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal(second.ToString(), AccessTokens.Text(again.AccessToken, "org_id"));
        Assert.NotNull(await LastUsedAtAsync(user, second));
    }

    [Fact]
    public async Task A_provider_link_waits_for_the_membership_before_it_locks_the_account()
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var company = await DevCompanyIdAsync();
        var ticket = await TicketAsync("google", GooglePerson());
        Task<HttpResponseMessage> link;
        await using (var held = await HeldLock.MembershipRowAsync(Factory, user.Id, company))
        {
            link = ProviderApi.Link(Client, ticket, UserPassword);
            await HeldLock.WaitForWaitersAsync(Factory, 1, link);

            await held.LockAccountAsync(user.Id);   // the link holds no account lock while it waits for the membership
            await held.DeleteMembershipAsync(user.Id, company);
            await held.ReleaseAsync();
        }

        using var response = await link;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
}
