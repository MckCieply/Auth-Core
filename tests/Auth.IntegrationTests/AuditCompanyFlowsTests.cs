using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Seeding;

namespace Auth.IntegrationTests;

public sealed class AuditCompanyFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    [Fact]
    public async Task A_company_made_by_the_operator_is_recorded_with_its_id_and_name()   // criterion 8
    {
        var run = await OperatorCli.RunAsync(Factory, "create-org", "--name", "Acme");
        Assert.Equal(0, run.Exit);
        var company = Guid.Parse(run.Out.Trim());

        var row = Assert.Single(await AuditAsync(AuditKinds.OrgCreated), r => r.OrgId == company);
        Assert.Equal("Acme", row.OrgName);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.SubjectUserId);
        Assert.Null(row.ClientIp);   // the CLI has no request
        Assert.Null(row.SubjectEmail);
        Assert.Null(row.TargetId);
        Assert.Equal("cli", AuditApi.Text(row, "via"));
        Assert.Equal(["via"], AuditApi.DetailNames(row));
    }

    [Fact]
    public async Task The_development_company_is_recorded_as_made_by_the_seed()
    {
        var row = Assert.Single(await AuditAsync(AuditKinds.OrgCreated));

        Assert.Equal(DevUserSeeder.DefaultOrgName, row.OrgName);
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal("seed", AuditApi.Text(row, "via"));
        Assert.Null(row.ActorUserId);
        Assert.Null(row.ClientIp);   // the seeder runs outside a request
        Assert.Null(row.SubjectUserId);
        Assert.Null(row.SubjectEmail);
        Assert.Null(row.TargetId);
        Assert.Equal(["via"], AuditApi.DetailNames(row));
    }

    [Fact]
    public async Task A_company_that_is_refused_leaves_no_row()
    {
        var before = (await AuditAsync(AuditKinds.OrgCreated)).Count;

        var run = await OperatorCli.RunAsync(Factory, "create-org", "--name", "   ");

        Assert.Equal(1, run.Exit);
        Assert.Equal(before, (await AuditAsync(AuditKinds.OrgCreated)).Count);
    }

    [Fact]
    public async Task A_rename_is_recorded_with_the_old_and_the_new_name_the_actor_and_the_address()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();

        using var response = await RateLimitApi.SendAsync(
            Client, HttpMethod.Patch, "/auth/org", "203.0.113.5", null, """{"name":"Acme Corp"}""");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);   // no token: refused, and nothing is recorded
        Assert.Empty(await AuditAsync(AuditKinds.OrgRenamed));

        using var renamed = await SendFromCallerAsync(HttpMethod.Patch, "/auth/org", token, new { name = "Acme Corp" });
        await TenancyApi.AssertEmptyAsync(renamed, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.OrgRenamed);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme Corp", row.OrgName);   // the name at that moment: the new one
        Assert.Equal("Acme", AuditApi.Text(row, "from"));
        Assert.Equal("Acme Corp", AuditApi.Text(row, "to"));
        Assert.Null(row.SubjectUserId);
        Assert.Null(row.SubjectEmail);
        Assert.Null(row.TargetId);
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal(["from", "to"], AuditApi.DetailNames(row));
    }

    [Fact]
    public async Task A_rename_that_is_refused_leaves_no_row()   // criterion 8: a failed change leaves no row
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var workerToken = (await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword)).AccessToken;
        Assert.NotEqual(Guid.Empty, worker);

        using var forbidden = await TenancyApi.Send(Client, HttpMethod.Patch, "/auth/org", workerToken, new { name = "Hijacked" });
        using var invalid = await TenancyApi.Send(Client, HttpMethod.Patch, "/auth/org", token, new { name = "   " });

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(await AuditAsync(AuditKinds.OrgRenamed));
    }
}
