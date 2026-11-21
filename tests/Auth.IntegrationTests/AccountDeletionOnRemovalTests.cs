using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Accounts are never left without a space: a removal and a company deletion delete the accounts.</summary>
public sealed class AccountDeletionOnRemovalTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Worker = "worker@acme.test";

    private Task<int> UsersAsync(Guid id) => InDbAsync(db => db.Users.CountAsync(u => u.Id == id, TestContext.Current.CancellationToken));

    private static async Task AssertUnknownAddressAsync(HttpResponseMessage login)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Removing_a_member_by_the_api_deletes_the_account_in_the_same_transaction()   // criterion 23
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");
        var session = await SessionApi.LoginAsync(Client, Worker, UserPassword);

        using (var removed = await SendFromCallerAsync(HttpMethod.Delete, $"/auth/org/members/{worker}", token))
        {
            await TenancyApi.AssertEmptyAsync(removed, HttpStatusCode.NoContent);   // the answer is unchanged
        }

        Assert.Equal(0, await UsersAsync(worker));
        Assert.Single(await AuditAsync(AuditKinds.MemberRemoved));
        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(worker, row.SubjectUserId);
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal("member_removed", AuditApi.Text(row, "reason"));
        Assert.Equal(["reason"], AuditApi.DetailNames(row));

        using (var refresh = await SessionApi.Refresh(Client, session.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(refresh);
        }

        using var login = await LoginApi.Login(Client, Worker, UserPassword);
        await AssertUnknownAddressAsync(login);
    }

    /// <summary>Sets when a membership was last used, as a switch or an earlier sign-in would have left it: the next sign-in lands there.</summary>
    private Task<int> SetLastUsedAtAsync(Guid userId, Guid companyId, DateTimeOffset at) =>
        InDbAsync(db => db.Memberships.Where(m => m.UserId == userId && m.CompanyId == companyId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedAt, (DateTimeOffset?)at), TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_member_with_another_membership_keeps_the_account_and_the_other_session()   // criterion 26
    {
        var (a, _, token) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var b = await CreateCompanyAsync("Globex");
        var worker = await AddMemberAsync(a, Worker, "user");
        await AddMembershipAsync(worker, b, "user");
        var inA = await SessionApi.LoginAsync(Client, Worker, UserPassword);
        Assert.Equal(a.ToString(), AccessTokens.Text(inA.AccessToken, "org_id"));
        await SetLastUsedAtAsync(worker, b, StorableTime.Now(Clock).AddHours(1));   // the next sign-in lands in B
        var inB = await SessionApi.LoginAsync(Client, Worker, UserPassword);
        Assert.Equal(b.ToString(), AccessTokens.Text(inB.AccessToken, "org_id"));

        using (var removed = await SendFromCallerAsync(HttpMethod.Delete, $"/auth/org/members/{worker}", token))
        {
            await TenancyApi.AssertEmptyAsync(removed, HttpStatusCode.NoContent);   // the answer does not show which it was
        }

        Assert.Equal(1, await UsersAsync(worker));
        Assert.Equal([b], await InDbAsync(db => db.Memberships.Where(m => m.UserId == worker).Select(m => m.CompanyId)
            .ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Single(await AuditAsync(AuditKinds.MemberRemoved));
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleted));

        var refreshedInB = await SessionApi.RefreshOk(Client, inB.RefreshToken);
        Assert.Equal(b.ToString(), AccessTokens.Text(refreshedInB.AccessToken, "org_id"));
        using var refusedInA = await SessionApi.Refresh(Client, inA.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refusedInA);
    }

    [Fact]
    public async Task A_member_without_another_membership_is_deleted_as_before()   // criterion 27
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");

        using (var removed = await SendFromCallerAsync(HttpMethod.Delete, $"/auth/org/members/{worker}", token))
        {
            await TenancyApi.AssertEmptyAsync(removed, HttpStatusCode.NoContent);
        }

        Assert.Equal(0, await UsersAsync(worker));
        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(worker, row.SubjectUserId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal("member_removed", AuditApi.Text(row, "reason"));
    }

    [Fact]
    public async Task A_later_invitation_to_the_address_creates_a_new_account()   // criterion 23
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");
        using (var removed = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", token))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        var invite = await AddInviteAsync(company, Worker, "user");
        await EnqueueInvitationAsync(invite, Worker);
        await DispatchAsync();
        using (var accepted = await TenancyApi.Accept(Client, TokenIn(Mail.Sent.Last(m => m.To == Worker)), "Newly-Chosen-1"))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        var again = await SessionApi.LoginAsync(Client, Worker, "Newly-Chosen-1");
        Assert.NotEqual(worker.ToString(), AccessTokens.Text(again.AccessToken, "sub"));
    }

    [Fact]
    public async Task Removing_a_member_by_the_cli_deletes_the_account_and_the_row_says_cli()   // criterion 23
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");

        var run = await OperatorCli.RunAsync(Factory, "remove-member", "--org", company.ToString(), "--email", Worker);

        Assert.Equal(AdminCli.Done, run.Exit);
        Assert.Equal("Member removed; the account is deleted.", run.Out.Trim());
        Assert.Equal(0, await UsersAsync(worker));
        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.ClientIp);
        Assert.Equal("cli", AuditApi.Text(row, "via"));
        Assert.Equal("member_removed", AuditApi.Text(row, "reason"));
        Assert.Single(await AuditAsync(AuditKinds.MemberRemoved));
    }

    [Fact]
    public async Task Deleting_a_company_by_the_api_deletes_every_members_account_the_callers_included()   // criterion 24
    {
        var (acme, admin, token) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var one = await AddMemberAsync(acme, "one@acme.test", "user");
        var two = await AddMemberAsync(acme, "two@acme.test", "user");
        var (_, globexAdmin, _) = await CompanyWithAdminAsync("Globex", "boss@globex.test");

        using (var deleted = await SendFromCallerAsync(HttpMethod.Delete, OrgEndpoints.OrgPath, token, new { name = "Acme", password = UserPassword }))
        {
            await TenancyApi.AssertEmptyAsync(deleted, HttpStatusCode.NoContent);
        }

        foreach (var id in new[] { admin, one, two })
        {
            Assert.Equal(0, await UsersAsync(id));
        }

        Assert.Equal(1, await UsersAsync(globexAdmin));
        var rows = await AuditAsync(AuditKinds.AccountDeleted);   // counted, never sorted by time: they share one transaction
        Assert.Equal(new[] { admin, one, two }.Order(), rows.Select(r => r.SubjectUserId!.Value).Order());
        Assert.All(rows, row =>
        {
            Assert.Equal("org_deleted", AuditApi.Text(row, "reason"));
            Assert.Equal(acme, row.OrgId);
            Assert.Equal("Acme", row.OrgName);
            Assert.Equal(admin, row.ActorUserId);
        });
        Assert.Single(await AuditAsync(AuditKinds.OrgDeleted));
    }

    [Fact]
    public async Task Deleting_a_company_by_the_cli_deletes_every_members_account()   // criterion 24
    {
        var (acme, admin, _) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var one = await AddMemberAsync(acme, "one@acme.test", "user");

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", acme.ToString(), "--confirm", "Acme");

        Assert.Equal(AdminCli.Done, run.Exit);
        Assert.Equal("Company deleted; members with no other space lose their accounts too.", run.Out.Trim());
        Assert.Equal(0, await UsersAsync(admin));
        Assert.Equal(0, await UsersAsync(one));
        var rows = await AuditAsync(AuditKinds.AccountDeleted);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Null(row.ActorUserId);
            Assert.Equal("cli", AuditApi.Text(row, "via"));
            Assert.Equal("org_deleted", AuditApi.Text(row, "reason"));
        });
    }

    [Fact]
    public async Task A_removed_persons_token_gets_permissions_changed_where_it_claims_and_forbidden_where_it_never_did()   // spec 0010, Amendment item 2
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");   // the role user claims no built-in permission
        var workerToken = (await SessionApi.LoginAsync(Client, Worker, UserPassword)).AccessToken;
        using (var removed = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", token))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var me = await TenancyApi.Get(Client, OrgEndpoints.MePath, workerToken);
        using var org = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, workerToken);
        using var members = await TenancyApi.Get(Client, "/auth/org/members", workerToken);

        await TenancyApi.AssertErrorAsync(me, HttpStatusCode.Forbidden, "permissions_changed");
        await TenancyApi.AssertErrorAsync(org, HttpStatusCode.Forbidden, "permissions_changed");
        await TenancyApi.AssertErrorAsync(members, HttpStatusCode.Forbidden, "forbidden");
    }
}