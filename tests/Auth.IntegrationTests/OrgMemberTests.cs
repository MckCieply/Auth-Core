using System.Net;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class OrgMemberTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string MembersPath = "/auth/org/members";

    private static string RolePath(Guid user) => $"{MembersPath}/{user}/role";

    private Task<HttpResponseMessage> ChangeRoleAsync(string token, Guid user, Guid role) =>
        TenancyApi.Send(Client, HttpMethod.Put, RolePath(user), token, new { role_id = role });

    private Task<HttpResponseMessage> RemoveAsync(string token, Guid user) =>
        TenancyApi.Send(Client, HttpMethod.Delete, $"{MembersPath}/{user}", token);

    private Task<Membership?> MembershipAsync(Guid user) =>
        InDbAsync(db => db.Memberships.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == user, TestContext.Current.CancellationToken));

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    // ---- listing

    [Fact]
    public async Task List_shows_the_members_sorted_by_address_with_their_roles_and_join_times()
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await AddMemberAsync(company, "zed@acme.test", "user");
        await AddMemberAsync(company, "Amy@acme.test", "user");

        using var response = await TenancyApi.Get(Client, MembersPath, token);

        var list = await TenancyApi.ReadOkAsync(response);
        Assert.Equal(["members"], list.EnumerateObject().Select(p => p.Name));
        var members = list.GetProperty("members").EnumerateArray().ToList();
        Assert.Equal(["Amy@acme.test", "boss@acme.test", "zed@acme.test"], members.Select(m => m.GetProperty("email").GetString()));   // ordinal
        Assert.Equal(["email", "joined_at", "role", "user_id"], members[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["id", "name"], members[0].GetProperty("role").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("user", members[0].GetProperty("role").GetProperty("name").GetString());
        Assert.Equal("admin", members[1].GetProperty("role").GetProperty("name").GetString());
        Assert.EndsWith("Z", members[0].GetProperty("joined_at").GetString());
        Assert.Equal((await InDbAsync(db => db.Users.Where(u => u.Email == "Amy@acme.test").Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken))).ToString(),
            members[0].GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task List_holds_no_member_of_another_company()   // criterion 14
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        var globex = await CreateCompanyAsync("Globex");
        await AddMemberAsync(globex, "carol@globex.test", "user");

        using var response = await TenancyApi.Get(Client, MembersPath, token);

        var emails = (await TenancyApi.ReadOkAsync(response)).GetProperty("members").EnumerateArray().Select(m => m.GetProperty("email").GetString());
        Assert.Equal(["boss@acme.test"], emails);
    }

    // ---- changing a role

    [Fact]
    public async Task Manager_changes_the_role_of_a_member()   // criterion 12
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");

        using var response = await ChangeRoleAsync(token, worker, await RoleIdAsync(company, "admin"));

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(await RoleIdAsync(company, "admin"), (await MembershipAsync(worker))!.RoleId);
    }

    [Fact]
    public async Task The_change_ends_no_session_and_reaches_the_members_next_refresh()   // criterion 12
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);

        using (var response = await ChangeRoleAsync(token, worker, await RoleIdAsync(company, "admin")))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Assert.Equal(["admin"], AccessTokens.Array(refreshed.AccessToken, "roles"));
        Assert.Contains("members:manage", AccessTokens.Array(refreshed.AccessToken, "permissions"));
        _ = await SessionApi.RefreshOk(Client, refreshed.RefreshToken);
    }

    [Fact]
    public async Task A_user_or_role_outside_the_callers_company_is_a_404_like_one_that_does_not_exist()   // criterion 14
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var globex = await CreateCompanyAsync("Globex");
        var stranger = await AddMemberAsync(globex, "carol@globex.test", "user");
        var own = await RoleIdAsync(company, "user");

        foreach (var (user, role) in new[] { (stranger, own), (Guid.NewGuid(), own), (worker, await RoleIdAsync(globex, "admin")), (worker, Guid.NewGuid()) })
        {
            using var response = await ChangeRoleAsync(token, user, role);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Equal(await RoleIdAsync(globex, "user"), (await MembershipAsync(stranger))!.RoleId);   // untouched
    }

    [Fact]
    public async Task Nobody_changes_their_own_role()   // criterion 17
    {
        var (company, admin, token) = await CompanyWithAdminAsync();

        foreach (var role in new[] { "user", "admin" })   // not even to the role they have
        {
            using var response = await ChangeRoleAsync(token, admin, await RoleIdAsync(company, role));
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "cannot_change_self");
        }

        Assert.Equal(await RoleIdAsync(company, "admin"), (await MembershipAsync(admin))!.RoleId);
    }

    [Fact]
    public async Task Rule_3_is_answered_before_rule_1_when_the_role_asked_for_is_their_own_change_and_too_big()   // spec 0005 → the order of checks
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage");
        var manager = await AddMemberAsync(company, "mgr@acme.test", "manager");
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;

        using var response = await ChangeRoleAsync(token, manager, await RoleIdAsync(company, "admin"));   // star: more than they hold

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "cannot_change_self");   // not permission_not_held
    }

    [Fact]
    public async Task Nobody_gives_a_role_that_holds_a_permission_they_lack()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage", "reports:read");
        await AddMemberAsync(company, "mgr@acme.test", "manager");
        var starter = await AddRoleAsync(company, "starter");   // a role the caller may touch: it holds nothing
        var worker = await AddMemberAsync(company, "worker@acme.test", "starter");
        var wider = await AddRoleAsync(company, "wider", "reports:read", "reports:approve");
        var narrower = await AddRoleAsync(company, "narrower", "reports:read");
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;

        using (var tooMuch = await ChangeRoleAsync(token, worker, wider))
        {
            await TenancyApi.AssertErrorAsync(tooMuch, HttpStatusCode.Forbidden, "permission_not_held");
        }

        using (var star = await ChangeRoleAsync(token, worker, await RoleIdAsync(company, "admin")))
        {
            await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        }

        Assert.Equal(starter, (await MembershipAsync(worker))!.RoleId);   // unchanged
        using var subset = await ChangeRoleAsync(token, worker, narrower);
        Assert.Equal(HttpStatusCode.NoContent, subset.StatusCode);
    }

    [Fact]
    public async Task A_caller_that_lists_every_permission_but_not_star_cannot_give_a_role_with_star()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        var everything = await AddRoleAsync(company, "everything", [.. Holder.Current.Catalog.Permissions]);
        await AddMemberAsync(company, "all@acme.test", "everything");
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var token = (await SessionApi.LoginAsync(Client, "all@acme.test", UserPassword)).AccessToken;

        using var star = await ChangeRoleAsync(token, worker, await RoleIdAsync(company, "admin"));
        await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        using var same = await ChangeRoleAsync(token, worker, everything);
        Assert.Equal(HttpStatusCode.NoContent, same.StatusCode);
    }

    // ---- removing

    [Fact]
    public async Task Removing_a_member_ends_the_membership_and_every_session_and_keeps_the_account()   // criterion 13
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var first = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        var second = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);

        using (var response = await RemoveAsync(token, worker))
        {
            await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        }

        Assert.Null(await MembershipAsync(worker));
        Assert.NotNull(await InDbAsync(db => db.Users.FindAsync([worker], TestContext.Current.CancellationToken).AsTask()));   // the account stays
        foreach (var session in new[] { first, second })
        {
            using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);
            await SessionApi.AssertInvalidGrantAsync(refresh);
        }

        using var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword);
        Assert.Equal("""{"error":"no_membership"}""", await login.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task A_removed_member_can_be_invited_again_but_the_old_sessions_stay_ended()   // criterion 13
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var old = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        var boss = (await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword)).AccessToken;
        using (var removed = await RemoveAsync(boss, worker))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        var globex = await CreateCompanyAsync("Globex");
        var invite = await AddInviteAsync(globex, "worker@acme.test", "user");
        await EnqueueInvitationAsync(invite, "worker@acme.test");
        await DispatchAsync();
        using (var accepted = await TenancyApi.Accept(Client, TokenIn(Assert.Single(Mail.Sent)), "Chosen-Passw0rd"))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        var again = await SessionApi.LoginAsync(Client, "worker@acme.test", "Chosen-Passw0rd");
        Assert.Equal(globex.ToString(), AccessTokens.Text(again.AccessToken, "org_id"));
        using var refresh = await SessionApi.Refresh(Client, old.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refresh);
    }

    [Fact]
    public async Task A_removed_members_access_token_is_refused_by_the_company_api_at_once()   // criterion 13
    {
        var (company, _, bossToken) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "admin");
        var workerToken = (await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword)).AccessToken;

        using (var removed = await RemoveAsync(bossToken, worker))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var response = await TenancyApi.Get(Client, MembersPath, workerToken);
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Fact]
    public async Task Nobody_removes_themselves()   // criterion 17
    {
        var (_, admin, token) = await CompanyWithAdminAsync();

        using var response = await RemoveAsync(token, admin);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "cannot_change_self");
        Assert.NotNull(await MembershipAsync(admin));
    }

    [Fact]
    public async Task Removing_someone_outside_the_company_or_unknown_is_a_404()   // criterion 14
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        var globex = await CreateCompanyAsync("Globex");
        var stranger = await AddMemberAsync(globex, "carol@globex.test", "user");

        foreach (var user in new[] { stranger, Guid.NewGuid() })
        {
            using var response = await RemoveAsync(token, user);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        Assert.NotNull(await MembershipAsync(stranger));
    }

    [Fact]
    public async Task Nobody_removes_a_member_who_holds_more_than_they_do()   // criterion 15: a lesser manager cannot push out an admin
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage");
        await AddMemberAsync(company, "mgr@acme.test", "manager");
        var admin = await AddMemberAsync(company, "boss@acme.test", "admin");   // another manager is left, through star
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;

        using var response = await RemoveAsync(token, admin);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permission_not_held");
        Assert.NotNull(await MembershipAsync(admin));
    }

    [Fact]
    public async Task Nobody_changes_the_role_of_a_member_who_holds_more_than_they_do_not_even_to_a_lesser_role()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage");
        await AddMemberAsync(company, "mgr@acme.test", "manager");
        var admin = await AddMemberAsync(company, "boss@acme.test", "admin");
        var plain = await AddRoleAsync(company, "plain");   // nothing: a role the caller could give
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;

        using var response = await ChangeRoleAsync(token, admin, plain);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permission_not_held");
        Assert.Equal(await RoleIdAsync(company, "admin"), (await MembershipAsync(admin))!.RoleId);
    }

    [Fact]
    public async Task A_caller_that_lists_every_permission_but_not_star_cannot_touch_a_member_with_star()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "everything", [.. Holder.Current.Catalog.Permissions]);
        await AddMemberAsync(company, "all@acme.test", "everything");
        var admin = await AddMemberAsync(company, "boss@acme.test", "admin");
        var token = (await SessionApi.LoginAsync(Client, "all@acme.test", UserPassword)).AccessToken;

        using var removed = await RemoveAsync(token, admin);
        using var changed = await ChangeRoleAsync(token, admin, await RoleIdAsync(company, "user"));

        await TenancyApi.AssertErrorAsync(removed, HttpStatusCode.Forbidden, "permission_not_held");
        await TenancyApi.AssertErrorAsync(changed, HttpStatusCode.Forbidden, "permission_not_held");
    }

    [Fact]
    public async Task A_member_whose_role_is_a_subset_of_the_callers_may_be_removed_and_re_roled()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage", "reports:read");
        await AddMemberAsync(company, "mgr@acme.test", "manager");
        var narrower = await AddRoleAsync(company, "narrower", "reports:read");
        var equal = await AddRoleAsync(company, "equal", "members:manage", "reports:read");
        var first = await AddMemberAsync(company, "first@acme.test", "narrower");
        var second = await AddMemberAsync(company, "second@acme.test", "equal");
        var third = await AddMemberAsync(company, "third@acme.test", "narrower");
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;

        using (var re = await ChangeRoleAsync(token, first, equal))   // from a subset to the caller's own permissions
        {
            Assert.Equal(HttpStatusCode.NoContent, re.StatusCode);
        }

        using (var down = await ChangeRoleAsync(token, second, narrower))   // a member with exactly the caller's permissions
        {
            Assert.Equal(HttpStatusCode.NoContent, down.StatusCode);
        }

        using var removed = await RemoveAsync(token, third);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Fact]
    public async Task The_operator_may_touch_any_member()   // the operator is exempt from rule 1
    {
        var company = await CreateCompanyAsync("Acme");
        await AddMemberAsync(company, "boss@acme.test", "admin");
        var second = await AddMemberAsync(company, "second@acme.test", "admin");
        var third = await AddMemberAsync(company, "third@acme.test", "admin");
        var plain = await RoleIdAsync(company, "user");

        var changed = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .ChangeRoleAsync(Actor.Operator, company, second, plain, TestContext.Current.CancellationToken));
        var removed = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .RemoveAsync(Actor.Operator, company, third, force: false, TestContext.Current.CancellationToken));

        Assert.True(changed.Succeeded, changed.Error);
        Assert.True(removed.Succeeded, removed.Error);
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("PUT", "/11111111-1111-1111-1111-111111111111/role")]
    [InlineData("DELETE", "/11111111-1111-1111-1111-111111111111")]
    public async Task Every_endpoint_needs_a_token_and_members_manage_checked_against_the_database(string method, string suffix)   // criterion 14
    {
        var company = await CreateCompanyAsync("Acme");
        var worker = await AddMemberAsync(company, "worker@acme.test", "admin");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        var path = MembersPath + suffix;
        object? body = method == "PUT" ? new { role_id = Guid.NewGuid() } : null;

        using (var anonymous = await TenancyApi.Send(Client, new HttpMethod(method), path, null, body))
        {
            await TenancyApi.AssertUnauthorizedAsync(anonymous);
        }

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "user"));
        using (var stale = await TenancyApi.Send(Client, new HttpMethod(method), path, session.AccessToken, body))
        {
            await TenancyApi.AssertErrorAsync(stale, HttpStatusCode.Forbidden, "permissions_changed");
        }

        var fresh = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using var plain = await TenancyApi.Send(Client, new HttpMethod(method), path, fresh.AccessToken, body);
        await TenancyApi.AssertErrorAsync(plain, HttpStatusCode.Forbidden, "forbidden");
    }

    [Theory]
    [InlineData("not-a-uuid", """{"role_id":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("11111111-1111-1111-1111-111111111111", """{"role_id":"not-a-uuid"}""")]
    [InlineData("11111111-1111-1111-1111-111111111111", "{}")]
    [InlineData("11111111-1111-1111-1111-111111111111", """{"role_id":7}""")]
    [InlineData("11111111-1111-1111-1111-111111111111", "not json")]
    public async Task A_malformed_id_or_body_is_a_400(string user, string body)
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        using var request = TenancyApi.Request(HttpMethod.Put, $"{MembersPath}/{user}/role", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_removal_with_a_path_that_is_not_a_uuid_is_a_400()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await TenancyApi.Send(Client, HttpMethod.Delete, $"{MembersPath}/not-a-uuid", token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    // ---- safety rule 2

    [Fact]
    public async Task The_operator_cannot_remove_the_last_manager_unless_forced()   // criteria 16, 22
    {
        var (company, admin, _) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");

        var refused = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .RemoveAsync(Actor.Operator, company, admin, force: false, TestContext.Current.CancellationToken));
        Assert.Equal("last_manager", refused.Error);
        Assert.NotNull(await MembershipAsync(admin));

        var plain = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .RemoveAsync(Actor.Operator, company, worker, force: false, TestContext.Current.CancellationToken));   // not a manager: fine
        Assert.True(plain.Succeeded);

        var forced = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .RemoveAsync(Actor.Operator, company, admin, force: true, TestContext.Current.CancellationToken));
        Assert.True(forced.Succeeded);
        Assert.Null(await MembershipAsync(admin));
    }

    [Fact]
    public async Task A_role_change_that_would_leave_no_manager_is_refused()   // criterion 16
    {
        var (company, admin, _) = await CompanyWithAdminAsync();
        var plain = await RoleIdAsync(company, "user");

        // Only the operator can ask this of the service: through the API a member who is asking is a manager themselves.
        var refused = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .ChangeRoleAsync(Actor.Operator, company, admin, plain, TestContext.Current.CancellationToken));

        Assert.Equal("last_manager", refused.Error);
        Assert.Equal(await RoleIdAsync(company, "admin"), (await MembershipAsync(admin))!.RoleId);
    }

    [Fact]
    public async Task A_company_without_a_manager_is_not_stopped_from_changing_a_plain_member()
    {
        var company = await CreateCompanyAsync("Acme");
        var first = await AddMemberAsync(company, "a@acme.test", "user");
        await AddMemberAsync(company, "b@acme.test", "user");
        var narrower = await AddRoleAsync(company, "narrower", "reports:read");

        var outcome = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .ChangeRoleAsync(Actor.Operator, company, first, narrower, TestContext.Current.CancellationToken));

        Assert.True(outcome.Succeeded);   // it had no manager before and has none after
    }

    [Fact]
    public async Task Two_managers_removing_each_other_at_once_leave_one_manager()   // criterion 16
    {
        for (var round = 0; round < 4; round++)
        {
            var company = await CreateCompanyAsync($"Acme {round}");
            await AddRoleAsync(company, "manager", "members:manage");
            var a = await AddMemberAsync(company, $"a{round}@acme.test", "manager");
            var b = await AddMemberAsync(company, $"b{round}@acme.test", "manager");
            var tokenA = (await SessionApi.LoginAsync(Client, $"a{round}@acme.test", UserPassword)).AccessToken;
            var tokenB = (await SessionApi.LoginAsync(Client, $"b{round}@acme.test", UserPassword)).AccessToken;

            var answers = await Task.WhenAll(RemoveAsync(tokenA, b), RemoveAsync(tokenB, a));
            try
            {
                var winner = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.NoContent);
                var loser = Assert.Single(answers, r => r != winner);
                await TenancyApi.AssertErrorAsync(loser, HttpStatusCode.Forbidden, "permissions_changed");   // removed before its turn
            }
            finally
            {
                foreach (var answer in answers)
                {
                    answer.Dispose();
                }
            }

            Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == company, TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task A_request_that_was_authorised_before_the_caller_was_removed_is_refused_under_the_lock()   // the race, made deterministic
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage");
        var a = await AddMemberAsync(company, "a@acme.test", "manager");
        var b = await AddMemberAsync(company, "b@acme.test", "manager");
        var asA = Actor.Of(await TenantOfAsync(a));   // what the endpoints of the two requests had read
        var asB = Actor.Of(await TenantOfAsync(b));

        var first = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .RemoveAsync(asA, company, b, force: false, TestContext.Current.CancellationToken));
        Assert.True(first.Succeeded);

        var second = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .RemoveAsync(asB, company, a, force: false, TestContext.Current.CancellationToken));

        Assert.Equal("permissions_changed", second.Error);
        Assert.NotNull(await MembershipAsync(a));
    }

    [Fact]
    public async Task A_caller_demoted_before_the_lock_cannot_change_a_role_or_invite()
    {
        var company = await CreateCompanyAsync("Acme");
        var boss = await AddMemberAsync(company, "boss@acme.test", "admin");
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var stale = Actor.Of(await TenantOfAsync(boss));
        await SetMemberRoleAsync(boss, await RoleIdAsync(company, "user"));   // demoted after the endpoint read them
        var admin = await RoleIdAsync(company, "admin");

        var change = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .ChangeRoleAsync(stale, company, worker, admin, TestContext.Current.CancellationToken));
        var invite = await InScopeAsync(sp => sp.GetRequiredService<InvitationService>()
            .SendAsync(stale, company, "new@acme.test", "NEW@ACME.TEST", admin, TestContext.Current.CancellationToken));

        Assert.Equal("permissions_changed", change.Error);
        Assert.Equal("permissions_changed", invite.Error);
        Assert.Equal(await RoleIdAsync(company, "user"), (await MembershipAsync(worker))!.RoleId);
        Assert.Equal(0, await InDbAsync(db => db.Invites.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Responses_are_never_stored_and_set_no_cookie()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await TenancyApi.Get(Client, MembersPath, token);

        AccountApi.AssertNeverStoredAndNoCookie(response);
    }
}
