using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;

namespace Auth.IntegrationTests;

public sealed class AuditMemberFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Worker = "worker@acme.test";

    [Fact]
    public async Task A_role_change_is_recorded_with_the_old_and_the_new_role_and_a_refused_one_is_not()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");
        var adminRole = await RoleIdAsync(company, "admin");

        using (var self = await TenancyApi.Send(Client, HttpMethod.Put, $"/auth/org/members/{admin}/role", token, new { role_id = adminRole }))
        {
            Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);   // cannot_change_self
        }

        using (var unknown = await TenancyApi.Send(Client, HttpMethod.Put, $"/auth/org/members/{worker}/role", token, new { role_id = Guid.NewGuid() }))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        Assert.Empty(await AuditAsync(AuditKinds.MemberRoleChanged));

        using var changed = await SendFromCallerAsync(HttpMethod.Put, $"/auth/org/members/{worker}/role", token, new { role_id = adminRole });
        await TenancyApi.AssertEmptyAsync(changed, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.MemberRoleChanged);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(worker, row.SubjectUserId);
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(adminRole, row.TargetId);   // the new role
        Assert.Equal("user", AuditApi.Text(row, "from"));
        Assert.Equal("admin", AuditApi.Text(row, "to"));
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal(["from", "to"], AuditApi.DetailNames(row));
    }

    [Fact]
    public async Task A_removed_member_is_recorded_with_the_role_they_had_and_a_refused_removal_is_not()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");

        using (var self = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{admin}", token))
        {
            Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        }

        Assert.Empty(await AuditAsync(AuditKinds.MemberRemoved));

        using var removed = await SendFromCallerAsync(HttpMethod.Delete, $"/auth/org/members/{worker}", token);
        await TenancyApi.AssertEmptyAsync(removed, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.MemberRemoved);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(worker, row.SubjectUserId);
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("user", AuditApi.Text(row, "role"));
        Assert.Null(AuditApi.Text(row, "forced"));
        Assert.Equal("Acme", row.OrgName);
        Assert.Null(row.TargetId);
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal(["role"], AuditApi.DetailNames(row));
    }

    [Fact]
    public async Task The_operator_removing_the_last_manager_by_force_is_recorded_as_forced_and_by_the_cli()   // criterion 8
    {
        var (company, admin, _) = await CompanyWithAdminAsync();

        var refused = await OperatorCli.RunAsync(Factory, "remove-member", "--org", company.ToString(), "--email", "boss@acme.test");
        Assert.Equal(1, refused.Exit);   // last_manager
        Assert.Empty(await AuditAsync(AuditKinds.MemberRemoved));

        var forced = await OperatorCli.RunAsync(Factory, "remove-member", "--org", company.ToString(), "--email", "boss@acme.test", "--force");
        Assert.Equal(0, forced.Exit);

        var row = await SingleAsync(AuditKinds.MemberRemoved);
        Assert.Null(row.ActorUserId);
        Assert.Equal(admin, row.SubjectUserId);
        Assert.Equal("cli", AuditApi.Text(row, "via"));
        Assert.Equal("true", AuditApi.Text(row, "forced")?.ToLowerInvariant());
        Assert.Equal("boss@acme.test", row.SubjectEmail);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal("admin", AuditApi.Text(row, "role"));
        Assert.Null(row.ClientIp);
    }
}
