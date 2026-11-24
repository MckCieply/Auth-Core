using System.Net;
using System.Text.Json;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0016 → the limit of <c>POST /auth/orgs</c> (<c>Auth:Tenancy:MaxCompaniesPerAccount</c>, here 2): what counts, and that nothing is written at the limit.</summary>
public sealed class OrgCreateLimitTests : AuditTestBase
{
    private const string Boss = "boss@acme.test";

    public OrgCreateLimitTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithOrgCreation().WithSetting(CompanyLimitSettings.Key, "2");
    }

    private async Task<(int Companies, int Memberships, int Rows)> CountsAsync() =>
        (await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)),
            await InDbAsync(db => db.Memberships.CountAsync(TestContext.Current.CancellationToken)),
            (await AuditAsync()).Count);

    private async Task<Guid> JoinNewCompanyAsync(Guid userId, string name)
    {
        var company = await CreateCompanyAsync(name);
        await AddMembershipAsync(userId, company, "admin");
        return company;
    }

    private static async Task<string?> ErrorOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    [Fact]
    public async Task An_account_at_the_limit_gets_409_and_nothing_is_written()   // criterion 9
    {
        var (_, admin, _) = await CompanyWithAdminAsync();
        await JoinNewCompanyAsync(admin, "Globex");
        var session = await SessionApi.LoginAsync(Client, Boss, UserPassword);
        var before = await CountsAsync();

        using var response = await TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Beta" });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "company_limit_reached");
        Assert.Equal(before, await CountsAsync());
        Assert.Equal(2, await CompanyCountOfAsync(admin));
    }

    [Fact]
    public async Task The_personal_space_is_not_counted()   // criterion 9
    {
        var (user, _) = await CreatePrivateAccountAsync("solo@example.test");
        await JoinNewCompanyAsync(user, "Globex");
        var session = await SessionApi.LoginAsync(Client, "solo@example.test", UserPassword);

        using (var second = await TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Second" }))
        {
            await TenancyApi.ReadCreatedAsync(second);
        }

        Assert.Equal(2, await CompanyCountOfAsync(user));
        using var third = await TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Third" });
        await TenancyApi.AssertErrorAsync(third, HttpStatusCode.Conflict, "company_limit_reached");
        Assert.Equal(2, await CompanyCountOfAsync(user));
    }

    [Fact]
    public async Task After_deleting_one_company_a_creation_succeeds()   // criterion 9
    {
        var (acme, admin, _) = await CompanyWithAdminAsync();
        await JoinNewCompanyAsync(admin, "Globex");
        var session = await SessionApi.LoginAsync(Client, Boss, UserPassword);
        var inAcme = await SessionApi.SwitchOk(Client, session.RefreshToken, acme);

        using (var full = await TenancyApi.CreateOrg(Client, inAcme.AccessToken, new { name = "Beta" }))
        {
            await TenancyApi.AssertErrorAsync(full, HttpStatusCode.Conflict, "company_limit_reached");
        }

        using (var deleted = await TenancyApi.Send(Client, HttpMethod.Delete, "/auth/org", inAcme.AccessToken, new { name = "Acme", password = UserPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var created = await TenancyApi.CreateOrg(Client, inAcme.AccessToken, new { name = "Beta" });
        await TenancyApi.ReadCreatedAsync(created);
        Assert.Equal(2, await CompanyCountOfAsync(admin));
    }

    [Fact]
    public async Task An_account_above_the_limit_by_invitations_gets_409()   // Review Focus 4
    {
        var (_, admin, _) = await CompanyWithAdminAsync();
        await JoinNewCompanyAsync(admin, "Globex");
        await JoinNewCompanyAsync(admin, "Initech");
        Assert.Equal(3, await CompanyCountOfAsync(admin));
        var session = await SessionApi.LoginAsync(Client, Boss, UserPassword);
        var before = await CountsAsync();

        using var response = await TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Beta" });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "company_limit_reached");
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task Invitation_acceptance_is_not_limited()   // Review Focus 4
    {
        // An account that already holds more companies than the limit: its invitation is joined (204) and the company-creation limit is ignored.
        var (_, admin, _) = await CompanyWithAdminAsync();
        await JoinNewCompanyAsync(admin, "Globex");
        await JoinNewCompanyAsync(admin, "Initech");
        var crowded = await CreateCompanyAsync("Umbrella");
        var invite = await AddInviteAsync(crowded, Boss, "user");
        await EnqueueInvitationAsync(invite, Boss);
        await DispatchAsync();
        using (var joined = await TenancyApi.Accept(Client, TokenIn(Mail.Sent.Last(m => m.To == Boss)), "Invited-Passw0rd"))
        {
            Assert.Equal(HttpStatusCode.NoContent, joined.StatusCode);   // spec 0017: an existing account joins whatever it holds
        }
        Assert.Equal(4, await CompanyCountOfAsync(admin));   // three before, one more by the join: the limit (2) did not stop it

        // An address with no account: the invitation makes the account and the membership, as ever.
        var other = await CreateCompanyAsync("Hooli");
        var second = await AddInviteAsync(other, "new@acme.test", "user");
        await EnqueueInvitationAsync(second, "new@acme.test");
        await DispatchAsync();
        using var accepted = await TenancyApi.Accept(Client, TokenIn(Mail.Sent.Last(m => m.To == "new@acme.test")), "Invited-Passw0rd");
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    [Fact]
    public async Task A_refused_creation_at_the_limit_writes_no_org_created_row()   // criterion 9
    {
        var (_, admin, _) = await CompanyWithAdminAsync();
        await JoinNewCompanyAsync(admin, "Globex");
        var session = await SessionApi.LoginAsync(Client, Boss, UserPassword);
        var before = (await AuditAsync(AuditKinds.OrgCreated)).Count;

        using var response = await TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Beta" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, (await AuditAsync(AuditKinds.OrgCreated)).Count);
    }
}
