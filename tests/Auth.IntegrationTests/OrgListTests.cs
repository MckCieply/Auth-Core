using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0015 → <c>GET /auth/orgs</c>: every membership of the caller, as the database says now, and which one the token is for.</summary>
public sealed class OrgListTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Path = "/auth/orgs";

    private static readonly string[] EntryMembers = ["current", "id", "kind", "name", "role"];

    private Task<int> SetLastUsedAtAsync(Guid userId, Guid companyId, DateTimeOffset at) =>
        InDbAsync(db => db.Memberships.Where(m => m.UserId == userId && m.CompanyId == companyId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedAt, (DateTimeOffset?)at), TestContext.Current.CancellationToken));

    private Task<Guid> RoleOfMembershipAsync(Guid userId, Guid spaceId) =>
        InDbAsync(db => db.Memberships.Where(m => m.UserId == userId && m.CompanyId == spaceId).Select(m => m.RoleId)
            .SingleAsync(TestContext.Current.CancellationToken));

    private async Task<string> TokenAsync(string email) => (await SessionApi.LoginAsync(Client, email, UserPassword)).AccessToken;

    private async Task<JsonElement> ListAsync(string token)
    {
        using var response = await TenancyApi.Get(Client, Path, token);
        return await TenancyApi.ReadOkAsync(response);
    }

    private static string[] NamesOf(JsonElement list) => [.. list.GetProperty("orgs").EnumerateArray().Select(e => e.GetProperty("name").GetString()!)];

    /// <summary>An account that is the first of the given companies' members, and a member of each of the others, joined in the order given.</summary>
    private async Task<Guid> AccountInAsync(string email, params (Guid Company, string Role)[] spaces)
    {
        var user = await AddMemberAsync(spaces[0].Company, email, spaces[0].Role);
        for (var i = 1; i < spaces.Length; i++)
        {
            await AddMembershipAsync(user, spaces[i].Company, spaces[i].Role);
        }

        return user;
    }

    [Fact]
    public async Task The_list_holds_every_membership_with_its_role_and_the_current_flag()   // criterion 12
    {
        var (user, personal) = await CreatePrivateAccountAsync("multi@example.test");
        var acme = await CreateCompanyAsync("Acme");
        var beta = await CreateCompanyAsync("beta");
        var joined = StorableTime.Now(Clock);
        await AddMembershipAsync(user, acme, "admin", joined.AddHours(1));
        await AddMembershipAsync(user, beta, "user", joined.AddHours(2));

        var list = await ListAsync(await TokenAsync("multi@example.test"));

        Assert.Equal(["orgs"], list.EnumerateObject().Select(p => p.Name));
        var entries = list.GetProperty("orgs").EnumerateArray().ToList();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(EntryMembers, entry.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal(["id", "name"], entry.GetProperty("role").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        });

        Assert.Equal([acme.ToString(), beta.ToString(), personal.ToString()], entries.Select(e => e.GetProperty("id").GetString()));
        Assert.Equal(["Acme", "beta", "private"], entries.Select(e => e.GetProperty("name").GetString()));
        Assert.Equal(["organization", "organization", "personal"], entries.Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal(["admin", "user", "private"], entries.Select(e => e.GetProperty("role").GetProperty("name").GetString()));
        Assert.Equal(
            [await RoleIdAsync(acme, "admin"), await RoleIdAsync(beta, "user"), await RoleOfMembershipAsync(user, personal)],
            entries.Select(e => e.GetProperty("role").GetProperty("id").GetGuid()));
        Assert.Equal([true, false, false], entries.Select(e => e.GetProperty("current").GetBoolean()));   // a sign-in lands in the company joined first
    }

    [Fact]
    public async Task The_order_is_by_name_ignoring_case_then_by_id()   // criterion 12
    {
        var zeta = await CreateCompanyAsync("Zeta");
        var acme = await CreateCompanyAsync("Acme");
        var beta = await CreateCompanyAsync("beta");
        await AccountInAsync("order@example.test", (zeta, "user"), (acme, "user"), (beta, "user"));

        var list = await ListAsync(await TokenAsync("order@example.test"));

        Assert.Equal(["Acme", "beta", "Zeta"], NamesOf(list));
    }

    [Fact]
    public async Task Names_that_differ_only_in_case_are_ordered_by_id()   // Review Focus 3
    {
        var variants = new[] { "Acme", "acme", "ACME", "aCme", "acMe", "ACme" };
        var ids = new List<Guid>();
        foreach (var name in variants)
        {
            ids.Add(await CreateCompanyAsync(name));
        }

        await AccountInAsync("same@example.test", [.. ids.Select(id => (id, "user"))]);

        var list = await ListAsync(await TokenAsync("same@example.test"));

        Assert.Equal(
            ids.Select(id => id.ToString()).Order(StringComparer.Ordinal),
            list.GetProperty("orgs").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Names_are_ordered_ordinally_not_by_culture()   // Review Focus 3
    {
        var umlaut = ((char)0xC4).ToString();   // after every ASCII letter by code point, next to A by culture
        var capital = await CreateCompanyAsync(umlaut);
        var b = await CreateCompanyAsync("B");
        var lowerZ = await CreateCompanyAsync("z");
        await AccountInAsync("ordinal@example.test", (capital, "user"), (b, "user"), (lowerZ, "user"));

        var list = await ListAsync(await TokenAsync("ordinal@example.test"));

        Assert.Equal(["B", "z", umlaut], NamesOf(list));
    }

    [Fact]
    public async Task Current_follows_the_token_company()   // criterion 13
    {
        var a = await CreateCompanyAsync("A company");
        var b = await CreateCompanyAsync("B company");
        var user = await AccountInAsync("both@example.test", (a, "user"), (b, "admin"));

        var tokenOfA = await TokenAsync("both@example.test");
        Assert.Equal(a.ToString(), AccessTokens.Text(tokenOfA, "org_id"));
        await SetLastUsedAtAsync(user, b, StorableTime.Now(Clock).AddHours(3));   // the next sign-in lands in B
        var tokenOfB = await TokenAsync("both@example.test");
        Assert.Equal(b.ToString(), AccessTokens.Text(tokenOfB, "org_id"));

        var listOfA = await ListAsync(tokenOfA);
        var listOfB = await ListAsync(tokenOfB);

        Assert.Equal([true, false], listOfA.GetProperty("orgs").EnumerateArray().Select(e => e.GetProperty("current").GetBoolean()));
        Assert.Equal([false, true], listOfB.GetProperty("orgs").EnumerateArray().Select(e => e.GetProperty("current").GetBoolean()));
        Assert.Equal(["A company", "B company"], NamesOf(listOfA));
        Assert.Equal(["A company", "B company"], NamesOf(listOfB));
    }

    [Fact]
    public async Task After_a_removal_from_the_token_company_no_entry_is_current()   // criterion 13
    {
        var a = await CreateCompanyAsync("A company");
        var b = await CreateCompanyAsync("B company");
        var user = await AccountInAsync("leaver@example.test", (a, "user"), (b, "user"));
        var token = await TokenAsync("leaver@example.test");
        Assert.Equal(a.ToString(), AccessTokens.Text(token, "org_id"));

        await RemoveMembershipAsync(user, a);
        var list = await ListAsync(token);

        Assert.Equal(["B company"], NamesOf(list));
        Assert.Equal([false], list.GetProperty("orgs").EnumerateArray().Select(e => e.GetProperty("current").GetBoolean()));
    }

    [Fact]
    public async Task Without_a_token_it_is_401()   // criterion 14
    {
        using var response = await TenancyApi.Get(Client, Path, null);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task For_a_deleted_account_it_is_403_permissions_changed()   // criterion 14
    {
        var (_, adminId, token) = await CompanyWithAdminAsync();

        await InDbAsync(async db =>
        {
            await db.Memberships.Where(m => m.UserId == adminId).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            return await db.Users.Where(u => u.Id == adminId).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        });
        using var response = await TenancyApi.Get(Client, Path, token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Fact]
    public async Task For_an_account_with_no_membership_it_is_403_permissions_changed()   // criterion 14
    {
        var (company, adminId, token) = await CompanyWithAdminAsync();

        await RemoveMembershipAsync(adminId, company);
        using var response = await TenancyApi.Get(Client, Path, token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Fact]
    public async Task A_token_whose_sub_is_not_an_id_is_403_forbidden()   // as CompanyAccess answers; no token this service issues says it
    {
        using var scope = Factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "not-a-guid"), new Claim("org_id", Guid.NewGuid().ToString())], "test"));
        using var body = new MemoryStream();
        context.Response.Body = body;

        var result = await OrgListEndpoint.ListAsync(
            context, scope.ServiceProvider.GetRequiredService<MembershipReader>());
        await result.ExecuteAsync(context);

        Assert.Equal((int)HttpStatusCode.Forbidden, context.Response.StatusCode);
        Assert.Equal("""{"error":"forbidden"}""", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task The_answer_is_never_stored()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await TenancyApi.Get(Client, Path, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains("no-cache", string.Join(",", response.Headers.Pragma.Select(p => p.ToString())), StringComparison.Ordinal);
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }

    [Fact]
    public async Task A_post_to_the_list_is_405_and_a_path_under_it_is_404()   // spec 0005's fallback for the new path
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using (var post = await TenancyApi.Send(Client, HttpMethod.Post, Path, token))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
            AccountApi.AssertNeverStoredAndNoCookie(post);
        }

        using var under = await TenancyApi.Get(Client, Path + "/nothing-here", token);
        Assert.Equal(HttpStatusCode.NotFound, under.StatusCode);
        Assert.Equal("", await under.Content.ReadAsStringAsync());
        AccountApi.AssertNeverStoredAndNoCookie(under);
    }

    [Fact]
    public async Task The_body_is_an_object_with_one_member_orgs_an_array_also_with_one_entry()
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var role = await RoleIdAsync(company, "admin");

        using var response = await TenancyApi.Get(Client, Path, token);

        var raw = await TenancyApi.ReadAsync(response, HttpStatusCode.OK);
        Assert.Equal(
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"orgs":[{"id":"{{company}}","name":"Acme","kind":"organization","role":{"id":"{{role}}","name":"admin"},"current":true}]}"""),
            raw);
    }
}
