using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>A change and its row are written in one transaction: with no table to write the row to, the change does not happen either.</summary>
public sealed class AuditAtomicityTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private static readonly string[] ReportsRead = ["reports:read"];

    [Fact]
    public async Task When_the_row_cannot_be_written_the_change_is_not_made()   // criterion 8: either both or neither
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var rename = await TenancyApi.Send(Client, HttpMethod.Patch, "/auth/org", token, new { name = "Acme Corp" });
        using var remove = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", token);
        using var role = await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/roles", token, new { name = "Auditor", permissions = ReportsRead });

        Assert.Equal(HttpStatusCode.InternalServerError, rename.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, remove.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, role.StatusCode);
        Assert.Equal("Acme", await InDbAsync(db => db.Companies.Where(c => c.Id == company).Select(c => c.Name).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == worker, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.Name == "Auditor", TestContext.Current.CancellationToken)));
    }
}
