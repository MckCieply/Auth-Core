using System.Net;
using System.Text;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class OrgEndpointTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private async Task<string> AdminTokenAsync() => (await SessionApi.LoginAsync(Client, Factory)).AccessToken;

    private async Task<string> TokenOfAsync(string email) => (await SessionApi.LoginAsync(Client, email, UserPassword)).AccessToken;

    [Fact]
    public async Task Me_returns_the_callers_current_data_from_the_database()   // criterion 19
    {
        var company = await DevCompanyIdAsync();
        var token = await AdminTokenAsync();

        using var response = await TenancyApi.Get(Client, OrgEndpoints.MePath, token);

        var me = await TenancyApi.ReadOkAsync(response);
        Assert.Equal(["email", "org_id", "org_name", "permissions", "roles", "sub"], me.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal((await Factory.SeedUserIdAsync()).ToString(), me.GetProperty("sub").GetString());
        Assert.Equal(Factory.SeedEmail, me.GetProperty("email").GetString());
        Assert.Equal(company.ToString(), me.GetProperty("org_id").GetString());
        Assert.Equal("Development", me.GetProperty("org_name").GetString());
        Assert.Equal(["admin"], me.GetProperty("roles").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(
            ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            me.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Me_follows_the_database_not_the_token()   // criterion 19
    {
        var company = await DevCompanyIdAsync();
        var worker = await AddMemberAsync(company, "worker@example.com", "admin");
        var token = await TokenOfAsync("worker@example.com");

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "user"));   // no refresh yet
        using var response = await TenancyApi.Get(Client, OrgEndpoints.MePath, token);

        var me = await TenancyApi.ReadOkAsync(response);
        Assert.Equal(["user"], me.GetProperty("roles").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["reports:approve", "reports:read"], me.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Me_of_someone_who_is_no_longer_a_member_is_permissions_changed()   // spec 0005 → GET /auth/me
    {
        var company = await DevCompanyIdAsync();
        var leaver = await AddMemberAsync(company, "leaver@example.com", "user");
        var token = await TokenOfAsync("leaver@example.com");

        await RemoveMembershipAsync(leaver);
        using var response = await TenancyApi.Get(Client, OrgEndpoints.MePath, token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Fact]
    public async Task Org_returns_the_callers_company_to_any_member()   // criterion 19
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "worker@example.com", "user");

        using var response = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, await TokenOfAsync("worker@example.com"));

        var org = await TenancyApi.ReadOkAsync(response);
        Assert.Equal(["id", "name"], org.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(company.ToString(), org.GetProperty("id").GetString());
        Assert.Equal("Development", org.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Org_shows_the_company_of_the_caller_and_not_another()
    {
        var other = await CreateCompanyAsync("Globex");
        await AddMemberAsync(other, "boss@globex.test", "admin");

        using var response = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, await TokenOfAsync("boss@globex.test"));

        var org = await TenancyApi.ReadOkAsync(response);
        Assert.Equal(other.ToString(), org.GetProperty("id").GetString());
        Assert.Equal("Globex", org.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Rename_changes_the_name_for_org_manage_and_only_that_company()   // criterion 19
    {
        var other = await CreateCompanyAsync("Globex");
        var token = await AdminTokenAsync();

        using (var renamed = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, token, new { name = "Acme Development" }))
        {
            await TenancyApi.AssertEmptyAsync(renamed, HttpStatusCode.NoContent);
        }

        using var after = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, token);
        Assert.Equal("Acme Development", (await TenancyApi.ReadOkAsync(after)).GetProperty("name").GetString());
        Assert.Equal("Globex", await InDbAsync(async db => (await db.Companies.SingleAsync(c => c.Id == other, TestContext.Current.CancellationToken)).Name));
    }

    [Fact]
    public async Task A_caller_demoted_before_the_lock_cannot_rename_the_company()   // the caller is read again under the lock, as on every write
    {
        var company = await CreateCompanyAsync("Acme");
        var boss = await AddMemberAsync(company, "boss@acme.test", "admin");
        var stale = Actor.Of(await TenantOfAsync(boss));
        await SetMemberRoleAsync(boss, await RoleIdAsync(company, "user"));   // demoted after the endpoint read them

        using var scope = Factory.Services.CreateScope();
        var renamed = await scope.ServiceProvider.GetRequiredService<CompanyService>()
            .RenameAsync(stale, company, "Mine now", TestContext.Current.CancellationToken);

        Assert.Equal("permissions_changed", renamed.Error);
        Assert.Equal("Acme", await InDbAsync(async db => (await db.Companies.SingleAsync(c => c.Id == company, TestContext.Current.CancellationToken)).Name));
    }

    [Fact]
    public async Task Rename_without_org_manage_is_forbidden()   // criterion 14
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "worker@example.com", "user");

        using var response = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, await TokenOfAsync("worker@example.com"), new { name = "Mine now" });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_demoted_caller_with_an_old_token_gets_permissions_changed_and_with_a_new_one_forbidden()   // criterion 14
    {
        var company = await DevCompanyIdAsync();
        var worker = await AddMemberAsync(company, "worker@example.com", "admin");
        var session = await SessionApi.LoginAsync(Client, "worker@example.com", UserPassword);

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "user"));

        using (var stale = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, session.AccessToken, new { name = "Hijacked" }))
        {
            await TenancyApi.AssertErrorAsync(stale, HttpStatusCode.Forbidden, "permissions_changed");
        }

        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using var fresh = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, refreshed.AccessToken, new { name = "Hijacked" });
        await TenancyApi.AssertErrorAsync(fresh, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_promoted_caller_may_use_the_new_permission_before_refreshing()   // the database decides
    {
        var company = await DevCompanyIdAsync();
        var worker = await AddMemberAsync(company, "worker@example.com", "user");
        var token = await TokenOfAsync("worker@example.com");

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "admin"));
        using var response = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, token, new { name = "Promoted" });

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_removed_member_with_a_live_token_gets_permissions_changed_on_every_endpoint()   // criterion 13, 14
    {
        var company = await DevCompanyIdAsync();
        var leaver = await AddMemberAsync(company, "leaver@example.com", "admin");
        var token = await TokenOfAsync("leaver@example.com");

        await RemoveMembershipAsync(leaver);

        using (var org = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, token))
        {
            await TenancyApi.AssertErrorAsync(org, HttpStatusCode.Forbidden, "permissions_changed");
        }

        using var rename = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, token, new { name = "Gone" });
        await TenancyApi.AssertErrorAsync(rename, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Padded")]
    [InlineData("Padded ")]
    [InlineData("Ac\tme")]
    public async Task Rename_to_a_name_that_breaks_the_rules_is_a_400(string name)
    {
        using var response = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, await AdminTokenAsync(), new { name });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Rename_to_a_name_of_101_characters_is_a_400_and_of_100_is_accepted()
    {
        var token = await AdminTokenAsync();

        using (var long101 = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, token, new { name = new string('a', 101) }))
        {
            await TenancyApi.AssertErrorAsync(long101, HttpStatusCode.BadRequest, "invalid_request");
        }

        using var long100 = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, token, new { name = new string('a', 100) });
        await TenancyApi.AssertEmptyAsync(long100, HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"name":42}""")]
    [InlineData("""{"name":null}""")]
    [InlineData("""["name"]""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task Rename_with_a_malformed_body_is_a_400(string body)
    {
        using var request = TenancyApi.Request(HttpMethod.Patch, OrgEndpoints.OrgPath, await AdminTokenAsync());
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Rename_with_the_wrong_content_type_or_an_oversized_body_is_a_400()
    {
        var token = await AdminTokenAsync();
        using (var request = TenancyApi.Request(HttpMethod.Patch, OrgEndpoints.OrgPath, token))
        {
            request.Content = new StringContent("""{"name":"Acme"}""", Encoding.UTF8, "text/plain");
            using var response = await Client.SendAsync(request);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }

        using var big = TenancyApi.Request(HttpMethod.Patch, OrgEndpoints.OrgPath, token);
        big.Content = new StringContent("{\"name\":\"Acme\",\"pad\":\"" + new string('x', 9 * 1024) + "\"}", Encoding.UTF8, "application/json");
        using var oversized = await Client.SendAsync(big);
        await TenancyApi.AssertErrorAsync(oversized, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Theory]
    [InlineData("GET", OrgEndpoints.MePath)]
    [InlineData("GET", OrgEndpoints.OrgPath)]
    [InlineData("PATCH", OrgEndpoints.OrgPath)]
    public async Task Every_endpoint_answers_401_without_a_valid_token(string method, string path)   // criterion 14
    {
        using var response = await TenancyApi.Send(Client, new HttpMethod(method), path, null, method == "PATCH" ? new { name = "x" } : null);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Authorisation_comes_before_the_shape_of_the_request()   // spec 0005 → the order of checks
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "worker@example.com", "user");
        using var request = TenancyApi.Request(HttpMethod.Patch, OrgEndpoints.OrgPath, await TokenOfAsync("worker@example.com"));
        request.Content = new StringContent("not json", Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");   // not 400
    }

    [Fact]
    public async Task An_unknown_path_under_org_is_the_frameworks_empty_404_and_is_still_never_stored()   // spec 0005 → Responses
    {
        using var response = await TenancyApi.Get(Client, "/auth/org/nothing-here", await AdminTokenAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }

    [Fact]
    public async Task Answers_never_set_the_refresh_cookie_and_are_never_stored()
    {
        var token = await AdminTokenAsync();

        using var response = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, token);

        AccountApi.AssertNeverStoredAndNoCookie(response);
    }

    [Fact]
    public async Task A_method_the_path_does_not_have_is_a_405_that_is_still_never_stored()
    {
        using var response = await TenancyApi.Send(Client, HttpMethod.Put, OrgEndpoints.OrgPath, await AdminTokenAsync());

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }
}
