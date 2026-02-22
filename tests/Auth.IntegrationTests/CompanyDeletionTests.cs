using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Auth.IntegrationTests;

public sealed class CompanyDeletionTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private Task<HttpResponseMessage> InviteAsync(string token, string email, Guid role) =>
        TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email, role_id = role });

    private async Task<int> CountAsync(Func<AuthDbContext, Task<int>> count) => await InDbAsync(count);

    [Fact]
    public async Task The_company_its_roles_members_invitations_and_their_queued_mails_are_deleted_and_nothing_else()   // criterion 10, 11
    {
        var (acme, acmeAdmin, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var worker = await AddMemberAsync(acme, "worker@acme.test", "user");
        var (globex, globexAdmin, globexToken) = await CompanyWithAdminAsync("Globex", "boss@globex.test");
        _ = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);   // the worker's session; the admins have theirs from CompanyWithAdminAsync
        using (var one = await InviteAsync(adminToken, "new@acme.test", await RoleIdAsync(acme, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, one.StatusCode);
        }

        using (var two = await InviteAsync(globexToken, "new@globex.test", await RoleIdAsync(globex, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, two.StatusCode);
        }

        Assert.Equal(2, await CountAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", acme.ToString(), "--confirm", "Acme");   // passes the Restrict keys on PostgreSQL

        Assert.True(run.Exit == 0, run.Error);
        Assert.Equal(0, await CountAsync(db => db.Companies.CountAsync(c => c.Id == acme, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CountAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == acme, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CountAsync(db => db.Memberships.CountAsync(m => m.CompanyId == acme, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CountAsync(db => db.Invites.CountAsync(i => i.CompanyId == acme, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));   // only the other company's mail is left

        // The other company is untouched, and the accounts stay.
        Assert.Equal(1, await CountAsync(db => db.Companies.CountAsync(c => c.Id == globex, TestContext.Current.CancellationToken)));
        Assert.Equal(2, await CountAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == globex, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Memberships.CountAsync(m => m.UserId == globexAdmin, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Invites.CountAsync(i => i.CompanyId == globex, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Users.CountAsync(u => u.Email == "boss@acme.test", TestContext.Current.CancellationToken)));

        // Read from the store, so the revocation is proved apart from the membership check, which would refuse these sessions anyway.
        await AssertSessionsAsync(acmeAdmin, revoked: true);
        await AssertSessionsAsync(worker, revoked: true);
        await AssertSessionsAsync(globexAdmin, revoked: false);
    }

    private async Task AssertSessionsAsync(Guid user, bool revoked)
    {
        var subject = user.ToString();
        using var scope = Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        var foundTokens = await tokens.FindBySubjectAsync(subject, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(foundTokens);
        foreach (var item in foundTokens)
        {
            Assert.Equal(revoked, await tokens.GetStatusAsync(item, TestContext.Current.CancellationToken) == OpenIddictConstants.Statuses.Revoked);
        }

        var foundGrants = await authorizations.FindBySubjectAsync(subject, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(foundGrants);
        foreach (var item in foundGrants)
        {
            Assert.Equal(revoked, await authorizations.GetStatusAsync(item, TestContext.Current.CancellationToken) == OpenIddictConstants.Statuses.Revoked);
        }
    }

    [Fact]
    public async Task What_a_member_sees_is_what_a_removed_member_sees()   // criterion 10, Decision 12
    {
        var (acme, _, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        await AddMemberAsync(acme, "worker@acme.test", "user");
        var worker = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        using (var invite = await InviteAsync(adminToken, "new@acme.test", await RoleIdAsync(acme, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, invite.StatusCode);
        }

        await DispatchAsync();
        var link = TokenIn(Mail.Sent[^1]);
        using (var preview = await TenancyApi.Preview(Client, link))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        }

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", acme.ToString(), "--confirm", "Acme");
        Assert.True(run.Exit == 0, run.Error);

        using (var me = await TenancyApi.Get(Client, "/auth/me", worker.AccessToken))
        {
            await TenancyApi.AssertErrorAsync(me, HttpStatusCode.Forbidden, "permissions_changed");   // an access token held: at once
        }

        using (var refresh = await SessionApi.Refresh(Client, worker.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(refresh);   // the next refresh: the app signs the person out
        }

        using (var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword))
        {
            await TenancyApi.AssertErrorAsync(login, HttpStatusCode.Forbidden, "no_membership");
        }

        using (var preview = await TenancyApi.Preview(Client, link))
        {
            await TenancyApi.AssertErrorAsync(preview, HttpStatusCode.BadRequest, "invalid_token");   // the pending link
        }

        using (var accept = await TenancyApi.Accept(Client, link, "Brand-New-Passw0rd"))
        {
            await TenancyApi.AssertErrorAsync(accept, HttpStatusCode.BadRequest, "invalid_token");
        }
    }

    [Fact]
    public async Task A_mail_queued_for_an_invitation_of_the_company_is_never_sent()   // criterion 10
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await OperatorCli.RunAsync(Factory, "invite", "--org", company.ToString(), "--email", "new@acme.test", "--role", "user");
        Assert.Equal(0, invite.Exit);
        Assert.Equal(1, await CountAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", company.ToString(), "--confirm", "Acme");
        Assert.True(run.Exit == 0, run.Error);

        await DispatchAsync();
        Assert.Empty(Mail.Attempted);
        Assert.Equal(0, await CountAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_deletion_is_recorded_with_the_company_the_counts_and_the_operator_mark()   // criterion 8
    {
        var (acme, _, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        await AddMemberAsync(acme, "worker@acme.test", "user");
        using (var invite = await InviteAsync(adminToken, "new@acme.test", await RoleIdAsync(acme, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, invite.StatusCode);
        }

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", acme.ToString(), "--confirm", "Acme");
        Assert.True(run.Exit == 0, run.Error);

        var row = await SingleAsync(AuditKinds.OrgDeleted);
        Assert.Equal(acme, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Null(row.ActorUserId);
        Assert.Equal("cli", AuditApi.Text(row, "via"));
        Assert.Equal("2", AuditApi.Text(row, "members"));
        Assert.Equal("1", AuditApi.Text(row, "invitations"));
        Assert.Equal("2", AuditApi.Text(row, "roles"));   // the two default roles, admin and user
        Assert.Equal(["invitations", "members", "roles", "via"], AuditApi.DetailNames(row));
        Assert.Null(row.ClientIp);
        Assert.Null(row.SubjectUserId);
        Assert.Null(row.TargetId);
    }
}
