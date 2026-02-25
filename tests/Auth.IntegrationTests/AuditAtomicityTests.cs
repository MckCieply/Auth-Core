using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
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

    [Fact]
    public async Task When_the_row_cannot_be_written_the_company_is_not_deleted()   // every change reaches the database before the save: only the transaction undoes them
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await AddMemberAsync(company, "worker@acme.test", "user");
        var worker = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        using (var sent = await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email = "new@acme.test", role_id = await RoleIdAsync(company, "user") }))
        {
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        }

        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", company.ToString(), "--confirm", "Acme");

        Assert.Equal(AdminCli.Failed, run.Exit);
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken)));
        Assert.Equal(2, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == company, TestContext.Current.CancellationToken)));
        Assert.Equal(2, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == company, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Invites.CountAsync(i => i.CompanyId == company, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.MailRequests.CountAsync(m => m.Kind == MailKind.Invitation, TestContext.Current.CancellationToken)));
        _ = await SessionApi.RefreshOk(Client, worker.RefreshToken);   // the revocations were rolled back with the rest
    }

    [Fact]
    public async Task When_the_row_cannot_be_written_the_invitation_is_not_made()
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var invite = await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email = "new@acme.test", role_id = user });

        Assert.Equal(HttpStatusCode.InternalServerError, invite.StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.Invites.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(m => m.Kind == MailKind.Invitation, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task When_the_row_cannot_be_written_the_acceptance_is_not_made()
    {
        const string email = "new@acme.test";
        var (company, _, token) = await CompanyWithAdminAsync();
        using (var sent = await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email, role_id = await RoleIdAsync(company, "user") }))
        {
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        }

        await DispatchAsync();
        var link = TokenIn(Mail.Sent[^1]);
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var accepted = await TenancyApi.Accept(Client, link, "Brand-New-Passw0rd");

        Assert.Equal(HttpStatusCode.InternalServerError, accepted.StatusCode);
        Assert.Equal(1, await InDbAsync(db => db.Invites.CountAsync(i => i.Email == email, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Email == email, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == company, TestContext.Current.CancellationToken)));
    }
}
