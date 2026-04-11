using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → The company API: every endpoint under /auth/org refuses a personal space, read from the database.</summary>
public sealed class PersonalSpaceRefusalTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Private = "ola@poczta.test";

    public static TheoryData<string, string> CompanyEndpoints => new()
    {
        { "GET", "/auth/org" },
        { "PATCH", "/auth/org" },
        { "DELETE", "/auth/org" },
        { "GET", "/auth/org/members" },
        { "PUT", "/auth/org/members/{id}/role" },
        { "DELETE", "/auth/org/members/{id}" },
        { "GET", "/auth/org/invites" },
        { "POST", "/auth/org/invites" },
        { "POST", "/auth/org/invites/{id}/resend" },
        { "DELETE", "/auth/org/invites/{id}" },
        { "GET", "/auth/org/roles" },
        { "POST", "/auth/org/roles" },
        { "PUT", "/auth/org/roles/{id}" },
        { "DELETE", "/auth/org/roles/{id}" },
    };

    /// <summary>A body each endpoint would take from a member who may use it: only the refusal of the space can stop it.</summary>
    private static object? BodyFor(string method, string path) => (method, path) switch
    {
        ("PATCH", "/auth/org") => new { name = "Renamed" },
        ("DELETE", "/auth/org") => new { name = PersonalSpaces.Name, password = UserPassword },
        ("PUT", "/auth/org/members/{id}/role") => new { role_id = Guid.NewGuid() },
        ("POST", "/auth/org/invites") => new { email = "new@poczta.test", role_id = Guid.NewGuid() },
        ("POST", "/auth/org/roles") or ("PUT", "/auth/org/roles/{id}") => new { name = "Reader", permissions = Array.Empty<string>() },
        _ => null,
    };

    [Theory]
    [MemberData(nameof(CompanyEndpoints))]
    public async Task Every_company_endpoint_answers_a_private_accounts_token_with_403_forbidden(string method, string path)   // criterion 14
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var token = (await SessionApi.LoginAsync(Client, Private, UserPassword)).AccessToken;

        using var response = await TenancyApi.Send(Client, new HttpMethod(method), path.Replace("{id}", user.ToString(), StringComparison.Ordinal), token, BodyFor(method, path));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        await AssertSpaceUntouchedAsync(user, space);
    }

    /// <summary>The space, its one role row and its one member are as they were, and no invitation or mail came out of the call.</summary>
    private async Task AssertSpaceUntouchedAsync(Guid user, Guid space)
    {
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space && c.Name == PersonalSpaces.Name, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user && m.CompanyId == space, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == space && r.Name == PersonalSpaces.RoleName, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == space, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.Invites.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Get_auth_me_stays_open_to_a_private_account()   // spec 0010 → The company API
    {
        await CreatePrivateAccountAsync(Private);
        var token = (await SessionApi.LoginAsync(Client, Private, UserPassword)).AccessToken;

        using var response = await TenancyApi.Get(Client, OrgEndpoints.MePath, token);

        Assert.Equal("personal", (await TenancyApi.ReadOkAsync(response)).GetProperty("org_kind").GetString());
    }

    [Theory]
    [MemberData(nameof(CompanyEndpoints))]
    public async Task A_token_that_says_organization_while_the_database_shows_a_personal_space_is_forbidden(string method, string path)   // "whatever the token says"
    {
        var company = await DevCompanyIdAsync();
        var user = await AddMemberAsync(company, "moved@poczta.test", "admin");
        var token = (await SessionApi.LoginAsync(Client, "moved@poczta.test", UserPassword)).AccessToken;   // org_kind organization, every permission
        await RemoveMembershipAsync(user);
        var space = await InDbAsync(async db =>
        {
            var id = PersonalSpaces.Add(db, user, StorableTime.Now(Clock));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return id;
        });

        using var response = await TenancyApi.Send(Client, new HttpMethod(method), path.Replace("{id}", user.ToString(), StringComparison.Ordinal), token, BodyFor(method, path));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");   // not permissions_changed: the space decides
        await AssertSpaceUntouchedAsync(user, space);
    }
}
