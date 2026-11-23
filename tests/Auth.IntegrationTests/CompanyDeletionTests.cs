using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
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

        // The other company is untouched; the accounts of the deleted company's members are gone with it (spec 0010).
        Assert.Equal(1, await CountAsync(db => db.Companies.CountAsync(c => c.Id == globex, TestContext.Current.CancellationToken)));
        Assert.Equal(2, await CountAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == globex, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Memberships.CountAsync(m => m.UserId == globexAdmin, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Invites.CountAsync(i => i.CompanyId == globex, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CountAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CountAsync(db => db.Users.CountAsync(u => u.Email == "boss@acme.test", TestContext.Current.CancellationToken)));

        // Read from the store: the sessions of the deleted accounts are gone with them; the other company's are untouched.
        await AssertNoSessionsAsync(acmeAdmin);
        await AssertNoSessionsAsync(worker);
        await AssertLiveSessionsAsync(globexAdmin);
    }

    private async Task AssertNoSessionsAsync(Guid user)
    {
        var subject = user.ToString();
        using var scope = Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        Assert.Empty(await tokens.FindBySubjectAsync(subject, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await authorizations.FindBySubjectAsync(subject, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
    }

    private async Task AssertLiveSessionsAsync(Guid user)
    {
        var subject = user.ToString();
        using var scope = Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        var foundTokens = await tokens.FindBySubjectAsync(subject, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(foundTokens);
        foreach (var item in foundTokens)
        {
            Assert.NotEqual(OpenIddictConstants.Statuses.Revoked, await tokens.GetStatusAsync(item, TestContext.Current.CancellationToken));
        }

        var foundGrants = await authorizations.FindBySubjectAsync(subject, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(foundGrants);
        foreach (var item in foundGrants)
        {
            Assert.NotEqual(OpenIddictConstants.Statuses.Revoked, await authorizations.GetStatusAsync(item, TestContext.Current.CancellationToken));
        }
    }

    private Task<Guid[]> MembershipsOfAsync(Guid user) =>
        InDbAsync(async db => (await db.Memberships.Where(m => m.UserId == user).Select(m => m.CompanyId)
            .ToListAsync(TestContext.Current.CancellationToken)).ToArray());

    /// <summary>A sign-in that lands in the company: its membership is marked as the one used last, and the login follows.</summary>
    private async Task<SessionApi.Session> LoginInAsync(string email, Guid user, Guid company)
    {
        await InDbAsync(db => db.Memberships.Where(m => m.UserId == user && m.CompanyId == company)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedAt, (DateTimeOffset?)StorableTime.Now(Clock).AddHours(1)), TestContext.Current.CancellationToken));
        var session = await SessionApi.LoginAsync(Client, email, UserPassword);
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        return session;
    }

    [Fact]
    public async Task A_member_who_gains_a_membership_while_the_company_is_deleted_keeps_the_account()   // spec 0017 recount
    {
        var (a, _, token) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var b = await CreateCompanyAsync("Globex");
        var worker = await AddMemberAsync(a, "worker@acme.test", "user");
        var roleInB = await RoleIdAsync(b, "user");

        Task<HttpResponseMessage> deletion;
        await using (var held = await HeldLock.AccountRowAsync(Factory, worker))
        {
            deletion = SendFromCallerAsync(HttpMethod.Delete, OrgEndpoints.OrgPath, token, new { name = "Acme", password = UserPassword });
            await HeldLock.WaitForWaitersAsync(Factory, 1, deletion);   // the deletion waits for the account row of the worker

            await held.InsertMembershipAsync(worker, b, roleInB);
            await held.ReleaseAsync();
        }

        using (var response = await deletion)
        {
            await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        }

        Assert.Equal(0, await CountAsync(db => db.Companies.CountAsync(c => c.Id == a, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
        Assert.Equal([b], await MembershipsOfAsync(worker));
        Assert.DoesNotContain(await AuditAsync(AuditKinds.AccountDeleted), r => r.SubjectUserId == worker);
    }

    [Fact]
    public async Task Members_with_another_membership_keep_their_accounts_and_the_others_are_deleted()   // criterion 28
    {
        var (a, boss, token) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var b = await CreateCompanyAsync("Globex");
        var shared = await AddMemberAsync(a, "shared@acme.test", "user");
        var only = await AddMemberAsync(a, "only@acme.test", "user");
        await AddMembershipAsync(boss, b, "user");
        await AddMembershipAsync(shared, b, "user");
        var bossInB = await LoginInAsync("boss@acme.test", boss, b);
        var sharedInB = await LoginInAsync("shared@acme.test", shared, b);

        using (var deleted = await SendFromCallerAsync(HttpMethod.Delete, OrgEndpoints.OrgPath, token, new { name = "Acme", password = UserPassword }))
        {
            await TenancyApi.AssertEmptyAsync(deleted, HttpStatusCode.NoContent);
        }

        Assert.Equal([b], await MembershipsOfAsync(boss));
        Assert.Equal([b], await MembershipsOfAsync(shared));
        Assert.Equal(1, await CountAsync(db => db.Users.CountAsync(u => u.Id == boss, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Users.CountAsync(u => u.Id == shared, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CountAsync(db => db.Users.CountAsync(u => u.Id == only, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CountAsync(db => db.Companies.CountAsync(c => c.Id == a, TestContext.Current.CancellationToken)));

        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(only, row.SubjectUserId);
        Assert.Equal(a, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal("org_deleted", AuditApi.Text(row, "reason"));
        Assert.Equal("3", AuditApi.Text(await SingleAsync(AuditKinds.OrgDeleted), "members"));   // every former member is counted

        foreach (var session in new[] { bossInB, sharedInB })
        {
            var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
            Assert.Equal(b.ToString(), AccessTokens.Text(refreshed.AccessToken, "org_id"));
        }
    }

    [Fact]
    public async Task An_admin_with_no_other_membership_is_deleted_with_the_company()
    {
        var (a, boss, token) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var b = await CreateCompanyAsync("Globex");
        var shared = await AddMemberAsync(a, "shared@acme.test", "user");
        await AddMembershipAsync(shared, b, "user");

        using (var deleted = await SendFromCallerAsync(HttpMethod.Delete, OrgEndpoints.OrgPath, token, new { name = "Acme", password = UserPassword }))
        {
            await TenancyApi.AssertEmptyAsync(deleted, HttpStatusCode.NoContent);
        }

        Assert.Equal(0, await CountAsync(db => db.Users.CountAsync(u => u.Id == boss, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Users.CountAsync(u => u.Id == shared, TestContext.Current.CancellationToken)));
        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(boss, row.SubjectUserId);
        Assert.Equal(boss, row.ActorUserId);
        Assert.Equal(a, row.OrgId);
        Assert.Equal("org_deleted", AuditApi.Text(row, "reason"));
    }

    [Fact]
    public async Task A_kept_members_role_does_not_stop_the_company_deletion()   // the memberships go before the roles: the foreign key to a role restricts
    {
        var (a, _, token) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var b = await CreateCompanyAsync("Globex");
        await AddRoleAsync(a, "manager", "members:manage");
        var kept = await AddMemberAsync(a, "kept@acme.test", "manager");
        await AddMembershipAsync(kept, b, "user");

        using (var deleted = await SendFromCallerAsync(HttpMethod.Delete, OrgEndpoints.OrgPath, token, new { name = "Acme", password = UserPassword }))
        {
            await TenancyApi.AssertEmptyAsync(deleted, HttpStatusCode.NoContent);
        }

        Assert.Equal(0, await CountAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == a, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CountAsync(db => db.Companies.CountAsync(c => c.Id == a, TestContext.Current.CancellationToken)));
        Assert.Equal([b], await MembershipsOfAsync(kept));
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
            await TenancyApi.AssertErrorAsync(login, HttpStatusCode.Unauthorized, "invalid_credentials");   // the account is gone (spec 0010)
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
