using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class OrgDeleteEndpointTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Boss = "boss@acme.test";

    private Task<HttpResponseMessage> DeleteAsync(string token, object? body) =>
        TenancyApi.Send(Client, HttpMethod.Delete, "/auth/org", token, body);

    private Task<HttpResponseMessage> DeleteAsync(string token, string name = "Acme", string? password = null) =>
        DeleteAsync(token, new { name, password = password ?? UserPassword });

    private Task<int> CompaniesAsync(Guid company) =>
        InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken));

    private Task<int> StreaksAsync() =>
        InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task The_admin_with_the_name_and_the_password_deletes_the_company_204()   // criterion 10
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        await AddMemberAsync(company, "worker@acme.test", "user");

        using var response = await DeleteAsync(token);

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(0, await CompaniesAsync(company));
        var row = await SingleAsync(AuditKinds.OrgDeleted);
        Assert.Equal(admin, row.ActorUserId);   // by a member, not by the operator
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Null(AuditApi.Text(row, "via"));
    }

    [Fact]
    public async Task What_a_member_sees_afterwards_is_what_a_removed_member_sees()   // criterion 10
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        Assert.NotEqual(Guid.Empty, worker);

        using (var response = await DeleteAsync(token))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var me = await TenancyApi.Get(Client, "/auth/me", session.AccessToken))
        {
            await TenancyApi.AssertErrorAsync(me, HttpStatusCode.Forbidden, "permissions_changed");
        }

        using (var refresh = await SessionApi.Refresh(Client, session.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(refresh);
        }

        using (var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword))
        {
            await TenancyApi.AssertErrorAsync(login, HttpStatusCode.Forbidden, "no_membership");
        }

        using var again = await DeleteAsync(token);   // the deleting admin's own token is just as stale
        await TenancyApi.AssertErrorAsync(again, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Fact]
    public async Task A_caller_without_the_permission_is_403_forbidden_before_anything_else_is_looked_at()   // table row 2
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        await AddMemberAsync(company, "worker@acme.test", "user");
        var workerToken = (await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword)).AccessToken;

        using var wrongEverything = await DeleteAsync(workerToken, name: "Wrong name", password: "Wrong-Password-1");
        using var noBody = await TenancyApi.Send(Client, HttpMethod.Delete, "/auth/org", workerToken);

        await TenancyApi.AssertErrorAsync(wrongEverything, HttpStatusCode.Forbidden, "forbidden");
        await TenancyApi.AssertErrorAsync(noBody, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(0, await StreaksAsync());   // nothing was counted
    }

    [Fact]
    public async Task A_caller_whose_token_no_longer_matches_the_database_is_403_permissions_changed()   // table row 2
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var plain = await RoleIdAsync(company, "user");
        await AddMemberAsync(company, "second@acme.test", "admin");   // so that the demotion leaves a manager
        await SetMemberRoleAsync(admin, plain);

        using var response = await DeleteAsync(token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
        Assert.Equal(1, await CompaniesAsync(company));
    }

    [Fact]
    public async Task Without_a_token_it_is_the_401_of_every_company_endpoint()
    {
        using var response = await TenancyApi.Send(Client, HttpMethod.Delete, "/auth/org", token: null, new { name = "Acme", password = UserPassword });

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"name":"Acme"}""")]
    [InlineData("""{"password":"Another-Passw0rd"}""")]
    [InlineData("""{"name":"Acme","password":""}""")]
    [InlineData("""{"name":"   ","password":"Another-Passw0rd"}""")]
    [InlineData("""{"name":1,"password":"Another-Passw0rd"}""")]
    [InlineData("""{"name":"Acme","password":["Another-Passw0rd"]}""")]
    public async Task A_malformed_body_is_400_invalid_request_and_counts_for_nothing(string body)   // table row 3
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/auth/org");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal(0, await StreaksAsync());
    }

    [Fact]
    public async Task A_password_with_a_nul_character_is_a_malformed_body()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await DeleteAsync(token, new { name = "Acme", password = "Another-Passw0rd" + (char)0 });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_content_type_that_is_not_json_is_400_and_one_that_is_not_utf_8_is_415()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var plain = new HttpRequestMessage(HttpMethod.Delete, "/auth/org");
        plain.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        plain.Content = new StringContent("""{"name":"Acme","password":"x"}""", System.Text.Encoding.UTF8, "text/plain");
        using var plainResponse = await Client.SendAsync(plain);

        using var utf16 = new HttpRequestMessage(HttpMethod.Delete, "/auth/org");
        utf16.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        utf16.Content = new StringContent("""{"name":"Acme","password":"x"}""", System.Text.Encoding.UTF8);
        utf16.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json; charset=utf-16");
        using var utf16Response = await Client.SendAsync(utf16);

        await TenancyApi.AssertErrorAsync(plainResponse, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, utf16Response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_403_wrong_password_and_is_recorded_and_the_company_stays()   // table row 5, criterion 10
    {
        var (company, admin, token) = await CompanyWithAdminAsync();

        using var response = await DeleteAsync(token, password: "Wrong-Password-1");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(1, await CompaniesAsync(company));
        var row = await SingleAsync(AuditKinds.OrgDeleteRefused);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal("wrong_password", AuditApi.Text(row, "reason"));
        await AssertNoSecretsAsync("Wrong-Password-1", UserPassword);
    }

    [Fact]
    public async Task The_password_is_checked_before_the_name_and_a_wrong_name_with_the_right_password_is_400()   // the order of the table
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        using var both = await DeleteAsync(token, name: "acme", password: "Wrong-Password-1");   // wrong name and wrong password
        Clock.Advance(LockoutApi.HumanPace);
        using var nameOnly = await DeleteAsync(token, name: "acme");                          // right password, name differs in case
        Clock.Advance(LockoutApi.HumanPace);
        using var trailing = await DeleteAsync(token, name: "Acme ");

        await TenancyApi.AssertErrorAsync(both, HttpStatusCode.Forbidden, "wrong_password");
        await TenancyApi.AssertErrorAsync(nameOnly, HttpStatusCode.BadRequest, "invalid_request");
        await TenancyApi.AssertErrorAsync(trailing, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(1, await CompaniesAsync(company));
    }

    [Fact]
    public async Task A_wrong_password_counts_in_the_login_streak_and_ten_of_them_lock_the_identifier()   // criterion 10
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        for (var i = 0; i < 10; i++)
        {
            Clock.Advance(LockoutApi.HumanPace);   // past the burst window of the lockout
            using var wrong = await DeleteAsync(token, password: "Wrong-Password-1");
            await TenancyApi.AssertErrorAsync(wrong, HttpStatusCode.Forbidden, "wrong_password");
        }

        Clock.Advance(LockoutApi.HumanPace);
        using var locked = await DeleteAsync(token);   // even the right password is refused now

        await LockoutApi.AssertLockedAsync(locked);
        Assert.Equal(1, await CompaniesAsync(company));
        var refusals = await AuditAsync(AuditKinds.OrgDeleteRefused);
        Assert.Equal(10, refusals.Count(r => AuditApi.Text(r, "reason") == "wrong_password"));
        Assert.Equal("locked", AuditApi.Text(Assert.Single(refusals, r => AuditApi.Text(r, "reason") == "locked"), "reason"));
    }

    [Fact]
    public async Task Failed_logins_and_wrong_passwords_here_add_up_in_one_streak()
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 9);   // nine failed logins

        Clock.Advance(LockoutApi.HumanPace);
        using var tenth = await DeleteAsync(token, password: "Wrong-Password-1");   // the tenth attempt of the streak
        Clock.Advance(LockoutApi.HumanPace);
        using var eleventh = await DeleteAsync(token);

        await TenancyApi.AssertErrorAsync(tenth, HttpStatusCode.Forbidden, "wrong_password");
        await LockoutApi.AssertLockedAsync(eleventh);
        Assert.Equal(1, await CompaniesAsync(company));
    }

    [Fact]
    public async Task A_locked_identifier_is_429_too_many_attempts_whatever_the_password_and_nothing_is_evaluated()   // table row 4
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 10);

        using var response = await DeleteAsync(token);

        await LockoutApi.AssertLockedAsync(response);
        Assert.Equal(1, await CompaniesAsync(company));
        var row = await SingleAsync(AuditKinds.OrgDeleteRefused);
        Assert.Equal("locked", AuditApi.Text(row, "reason"));
        Assert.Equal(admin, row.ActorUserId);
    }

    [Fact]
    public async Task The_lockout_comes_before_the_password_and_a_malformed_body_before_the_lockout()   // the order of the table
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 10);

        using var locked = await DeleteAsync(token, password: "Wrong-Password-1");
        using var malformed = await DeleteAsync(token, new { name = "Acme" });

        await LockoutApi.AssertLockedAsync(locked);
        await TenancyApi.AssertErrorAsync(malformed, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_correct_password_ends_the_streak_even_when_the_name_is_wrong()   // the password is evaluated once, as login does it
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 4);
        Assert.Equal(1, await StreaksAsync());

        Clock.Advance(LockoutApi.HumanPace);
        using var response = await DeleteAsync(token, name: "Not the name");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, await StreaksAsync());
    }

    [Fact]
    public async Task A_member_of_the_company_who_holds_a_permission_the_caller_does_not_makes_it_403_permission_not_held()   // table row 6, rule 1
    {
        var (company, _, _) = await CompanyWithAdminAsync();   // the admin holds *
        await AddRoleAsync(company, "Deleter", "org:delete");
        await AddMemberAsync(company, "deleter@acme.test", "Deleter");
        var token = (await SessionApi.LoginAsync(Client, "deleter@acme.test", UserPassword)).AccessToken;

        using var response = await DeleteAsync(token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permission_not_held");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleted));
    }

    [Fact]
    public async Task Rule_1_comes_after_the_name_and_the_password()
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        await AddRoleAsync(company, "Deleter", "org:delete");
        await AddMemberAsync(company, "deleter@acme.test", "Deleter");
        var token = (await SessionApi.LoginAsync(Client, "deleter@acme.test", UserPassword)).AccessToken;

        using var wrongName = await DeleteAsync(token, name: "Wrong name");
        Clock.Advance(LockoutApi.HumanPace);
        using var wrongPassword = await DeleteAsync(token, password: "Wrong-Password-1");

        await TenancyApi.AssertErrorAsync(wrongName, HttpStatusCode.BadRequest, "invalid_request");
        await TenancyApi.AssertErrorAsync(wrongPassword, HttpStatusCode.Forbidden, "wrong_password");
    }

    [Fact]
    public async Task A_caller_who_holds_org_delete_without_star_may_delete_a_company_of_lesser_members()
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        await AddRoleAsync(company, "Deleter", "org:delete", "reports:read");
        await AddMemberAsync(company, "deleter@acme.test", "Deleter");
        // the admin holds * and is a member, so the deleter cannot delete: remove them first through the operator
        var run = await OperatorCli.RunAsync(Factory, "remove-member", "--org", company.ToString(), "--email", Boss, "--force");
        Assert.Equal(0, run.Exit);
        var token = (await SessionApi.LoginAsync(Client, "deleter@acme.test", UserPassword)).AccessToken;

        using var response = await DeleteAsync(token);

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(0, await CompaniesAsync(company));
    }

    [Fact]
    public async Task Two_deletions_at_once_give_one_204_and_one_403_permissions_changed()   // the lock, and the company that has vanished
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        var responses = await Task.WhenAll(DeleteAsync(token), DeleteAsync(token));

        try
        {
            Assert.Equal(
                [HttpStatusCode.NoContent, HttpStatusCode.Forbidden],
                responses.Select(r => r.StatusCode).Order());   // 204 before 403
            var refused = responses.Single(r => r.StatusCode == HttpStatusCode.Forbidden);
            await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.Forbidden, "permissions_changed");
            Assert.Equal(0, await CompaniesAsync(company));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}
