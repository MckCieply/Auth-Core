using System.Net;
using System.Text.Json;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class AuditRoleFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private static readonly string[] ReportsRead = ["reports:read"];
    private static readonly string[] TemplatesManage = ["templates:manage"];
    private static readonly string[] ReportsReadAndTemplates = ["reports:read", "templates:manage"];

    private Task<HttpResponseMessage> CreateAsync(string token, string name, params string[] permissions) =>
        TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/roles", token, new { name, permissions });

    [Fact]
    public async Task A_created_role_is_recorded_with_its_name_and_permissions_and_a_refused_one_is_not()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();

        using (var taken = await CreateAsync(token, "USER", "reports:read"))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);   // role_name_taken
        }

        using (var unknown = await CreateAsync(token, "Auditor", "nothing:here"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);   // unknown_permission
        }

        Assert.Empty(await AuditAsync(AuditKinds.RoleCreated));

        using var created = await SendFromCallerAsync(
            HttpMethod.Post, "/auth/org/roles", token, new { name = "Auditor", permissions = ReportsReadAndTemplates });
        var role = await TenancyApi.ReadCreatedAsync(created);

        var row = await SingleAsync(AuditKinds.RoleCreated);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(Guid.Parse(role.GetProperty("id").GetString()!), row.TargetId);
        Assert.Equal("Auditor", AuditApi.Text(row, "name"));
        Assert.Equal("""["reports:read","templates:manage"]""", AuditApi.Raw(row, "permissions"));
        Assert.Null(row.SubjectUserId);
        Assert.Null(row.SubjectEmail);
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal(["name", "permissions"], AuditApi.DetailNames(row));
    }

    [Fact]
    public async Task A_replaced_role_is_recorded_with_what_it_was_and_a_refused_replacement_is_not()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");
        var before = await InDbAsync(db => db.CompanyRoles.Where(r => r.Id == user).Select(r => r.Permissions).SingleAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(before);

        using (var taken = await TenancyApi.Send(Client, HttpMethod.Put, $"/auth/org/roles/{user}", token, new { name = "admin", permissions = ReportsRead }))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        }

        using (var lastManager = await TenancyApi.Send(
            Client, HttpMethod.Put, $"/auth/org/roles/{await RoleIdAsync(company, "admin")}", token, new { name = "admin", permissions = ReportsRead }))
        {
            Assert.Equal(HttpStatusCode.Conflict, lastManager.StatusCode);   // the company would have no manager
        }

        Assert.Empty(await AuditAsync(AuditKinds.RoleUpdated));

        using var replaced = await SendFromCallerAsync(HttpMethod.Put, $"/auth/org/roles/{user}", token, new { name = "Staff", permissions = TemplatesManage });
        await TenancyApi.AssertEmptyAsync(replaced, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.RoleUpdated);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(user, row.TargetId);
        Assert.Equal("Staff", AuditApi.Text(row, "name"));
        Assert.Equal("user", AuditApi.Text(row, "previous_name"));
        Assert.Equal("""["templates:manage"]""", AuditApi.Raw(row, "permissions"));
        Assert.Equal(JsonSerializer.Serialize(before), AuditApi.Raw(row, "previous_permissions"));   // what the role held before
        Assert.Null(row.SubjectUserId);
        Assert.Null(row.SubjectEmail);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal(["name", "permissions", "previous_name", "previous_permissions"], AuditApi.DetailNames(row));
    }

    [Fact]
    public async Task A_deleted_role_is_recorded_and_a_role_in_use_is_not()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var spare = await AddRoleAsync(company, "Spare", "reports:read");
        var user = await RoleIdAsync(company, "user");
        await AddMemberAsync(company, "worker@acme.test", "user");

        using (var inUse = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/roles/{user}", token))
        {
            Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);   // role_in_use
        }

        Assert.Empty(await AuditAsync(AuditKinds.RoleDeleted));

        using var deleted = await SendFromCallerAsync(HttpMethod.Delete, $"/auth/org/roles/{spare}", token);
        await TenancyApi.AssertEmptyAsync(deleted, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.RoleDeleted);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(spare, row.TargetId);
        Assert.Equal("Spare", AuditApi.Text(row, "name"));
        Assert.Equal("""["reports:read"]""", AuditApi.Raw(row, "permissions"));
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Null(row.SubjectUserId);
        Assert.Null(row.SubjectEmail);
        Assert.Equal(["name", "permissions"], AuditApi.DetailNames(row));
    }
}
