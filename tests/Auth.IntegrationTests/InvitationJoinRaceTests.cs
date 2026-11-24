using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0017, criteria 18 and 20 and the lock order of the acceptance: a join into another company holds the account row, so a removal, a
/// company deletion or a self-deletion either sees the new membership (and keeps the account) or deletes the account first (and the acceptance
/// makes a new one). Each race holds a lock of the test and lets the requests queue behind it in the order named, as
/// <c>PrivateAccountRaceTests</c> does.
/// </summary>
public sealed class InvitationJoinRaceTests : TenancyTestBase
{
    private const string WorkerAddress = "worker@acme.test";
    private const string WorkerNormalized = "WORKER@ACME.TEST";
    private const string Private = "ola@poczta.test";
    private const string PrivateNormalized = "OLA@POCZTA.TEST";
    private const string InvitedPassword = "Invited-Passw0rd";

    public InvitationJoinRaceTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup();
    }

    /// <summary>The worker: a plain member of Acme only, with Acme's admin logged in, Globex and a dispatched invitation of Globex for the worker.</summary>
    private async Task<(Guid Acme, Guid Globex, Guid Worker, string AdminToken, string InviteToken)> WorkerAsync()
    {
        var (acme, _, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var worker = await AddMemberAsync(acme, WorkerAddress, "user");
        var globex = await CreateCompanyAsync("Globex");
        var token = await InvitationTokenAsync(globex, WorkerAddress);
        return (acme, globex, worker, adminToken, token);
    }

    private Task<int> CountAsync(string kind, Guid? subject = null) =>
        InDbAsync(db => db.AuditEvents.CountAsync(e => e.Kind == kind && (subject == null || e.SubjectUserId == subject), TestContext.Current.CancellationToken));

    private Task<List<Guid>> CompaniesOfAsync(Guid user) =>
        InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.UserId == user).Select(m => m.CompanyId).ToListAsync(TestContext.Current.CancellationToken));

    private Task<int> UsersAsync(Guid user) =>
        InDbAsync(db => db.Users.CountAsync(u => u.Id == user, TestContext.Current.CancellationToken));

    [Fact]
    public async Task An_acceptance_that_holds_the_account_row_keeps_the_account_from_a_removal()   // criterion 18, order A
    {
        var (_, globex, worker, adminToken, token) = await WorkerAsync();

        Task<HttpResponseMessage> acceptance;
        Task<HttpResponseMessage> removal;
        await using (var held = await HeldLock.AuditTableAsync(Factory))
        {
            acceptance = TenancyApi.AcceptWithoutPassword(Client, token);
            await HeldLock.WaitForWaitersAsync(Factory, 1, acceptance);   // the account row locked, the membership added, waiting at its audit row

            removal = TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", adminToken);
            await HeldLock.WaitForWaitersAsync(Factory, 2, acceptance, removal);   // the removal waits at the account row

            await held.ReleaseAsync();
        }

        using var accepted = await acceptance;
        using var removed = await removal;
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(1, await UsersAsync(worker));
        Assert.Equal([globex], await CompaniesOfAsync(worker));
        Assert.Equal(1, await CountAsync(AuditKinds.MemberRemoved));
        Assert.Equal(0, await CountAsync(AuditKinds.AccountDeleted));
    }

    [Fact]
    public async Task An_acceptance_that_holds_the_account_row_keeps_the_account_from_remove_member_and_the_cli_says_kept()   // criterion 18, the CLI
    {
        var (acme, globex, worker, _, token) = await WorkerAsync();

        Task<HttpResponseMessage> acceptance;
        Task<OperatorCli.Result> removal;
        await using (var held = await HeldLock.AuditTableAsync(Factory))
        {
            acceptance = TenancyApi.AcceptWithoutPassword(Client, token);
            await HeldLock.WaitForWaitersAsync(Factory, 1, acceptance);

            removal = OperatorCli.RunAsync(Factory, "remove-member", "--org", acme.ToString(), "--email", WorkerAddress);
            await HeldLock.WaitForWaitersAsync(Factory, 2, acceptance);   // the CLI is not an HTTP task: only the acceptance can finish early

            await held.ReleaseAsync();
        }

        using var accepted = await acceptance;
        var run = await removal;
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(AdminCli.Done, run.Exit);
        Assert.Equal("Member removed; the account is kept.", run.Out.Trim());
        Assert.Equal(1, await UsersAsync(worker));
        Assert.Equal([globex], await CompaniesOfAsync(worker));
        Assert.Equal(1, await CountAsync(AuditKinds.MemberRemoved));
        Assert.Equal(0, await CountAsync(AuditKinds.AccountDeleted));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_company_deletion_and_an_acceptance_keep_or_remake_the_account_in_both_orders(bool acceptanceFirst)   // criterion 18, company deletion
    {
        var (acme, globex, worker, adminToken, token) = await WorkerAsync();

        Task<HttpResponseMessage> acceptance;
        Task<HttpResponseMessage> deletion;
        await using (var held = await HeldLock.AuditTableAsync(Factory))
        {
            if (acceptanceFirst)
            {
                acceptance = TenancyApi.AcceptWithoutPassword(Client, token);
                await HeldLock.WaitForWaitersAsync(Factory, 1, acceptance);
                deletion = TenancyApi.Send(Client, HttpMethod.Delete, OrgEndpoints.OrgPath, adminToken, new { name = "Acme", password = UserPassword });
                await HeldLock.WaitForWaitersAsync(Factory, 2, acceptance, deletion);
            }
            else
            {
                deletion = TenancyApi.Send(Client, HttpMethod.Delete, OrgEndpoints.OrgPath, adminToken, new { name = "Acme", password = UserPassword });
                await HeldLock.WaitForWaitersAsync(Factory, 1, deletion);
                acceptance = TenancyApi.Accept(Client, token, InvitedPassword);
                await HeldLock.WaitForWaitersAsync(Factory, 2, deletion, acceptance);
            }

            await held.ReleaseAsync();
        }

        using var accepted = await acceptance;
        using var deleted = await deletion;
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(1, await CountAsync(AuditKinds.OrgDeleted));
        if (acceptanceFirst)
        {
            Assert.Equal(1, await UsersAsync(worker));
            Assert.Equal([globex], await CompaniesOfAsync(worker));
            Assert.Equal(0, await CountAsync(AuditKinds.AccountDeleted, worker));
        }
        else
        {
            Assert.Equal(0, await UsersAsync(worker));
            Assert.Equal(1, await CountAsync(AuditKinds.AccountDeleted, worker));
            var row = await InDbAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(
                e => e.Kind == AuditKinds.AccountDeleted && e.SubjectUserId == worker, TestContext.Current.CancellationToken));
            Assert.Equal("org_deleted", AuditApi.Text(row, "reason"));
            var users = await InDbAsync(db => db.Users.AsNoTracking().Where(u => u.NormalizedEmail == WorkerNormalized).Select(u => u.Id).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal([globex], await CompaniesOfAsync(Assert.Single(users)));
        }

        Assert.Equal(0, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == acme, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_self_deletion_and_an_acceptance_of_one_private_account_never_500_in_either_order(bool acceptanceFirst)   // criterion 20
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var acme = await CreateCompanyAsync("Acme");
        var token = await InvitationTokenAsync(acme, Private);

        Task<HttpResponseMessage> Delete() => TenancyApi.Send(Client, HttpMethod.Delete, "/auth/account", session.AccessToken, new { password = UserPassword });
        Task<HttpResponseMessage> Accept() => TenancyApi.Accept(Client, token, InvitedPassword);

        Task<HttpResponseMessage> acceptance;
        Task<HttpResponseMessage> deletion;
        await using (var held = await HeldLock.AddressAsync(Factory, PrivateNormalized))
        {
            if (acceptanceFirst)
            {
                acceptance = Accept();
                await HeldLock.WaitForWaitersAsync(Factory, 1, acceptance);
                deletion = Delete();
                await HeldLock.WaitForWaitersAsync(Factory, 2, acceptance, deletion);
            }
            else
            {
                deletion = Delete();
                await HeldLock.WaitForWaitersAsync(Factory, 1, deletion);
                acceptance = Accept();
                await HeldLock.WaitForWaitersAsync(Factory, 2, deletion, acceptance);
            }

            await held.ReleaseAsync();
        }

        using var accepted = await acceptance;
        using var deleted = await deletion;
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        if (acceptanceFirst)
        {
            await TenancyApi.AssertErrorAsync(deleted, HttpStatusCode.Forbidden, "company_member");
            Assert.Equal(1, await UsersAsync(user));
            Assert.Equal(new[] { space, acme }.Order(), (await CompaniesOfAsync(user)).Order());   // the personal space and the company
        }
        else
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Equal(0, await UsersAsync(user));
            var users = await InDbAsync(db => db.Users.AsNoTracking().Where(u => u.NormalizedEmail == PrivateNormalized).Select(u => u.Id).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal([acme], await CompaniesOfAsync(Assert.Single(users)));   // a new account, a member of Acme only
        }
    }

    [Fact]
    public async Task The_move_waits_for_a_held_personal_membership_before_it_locks_the_account()   // lock order; Review Focus 4
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var acme = await CreateCompanyAsync("Acme");
        var token = await InvitationTokenAsync(acme, Private);

        Task<HttpResponseMessage> move;
        await using (var held = await HeldLock.MembershipRowAsync(Factory, user, space))
        {
            move = TenancyApi.AcceptLeaving(Client, token);
            await HeldLock.WaitForWaitersAsync(Factory, 1, move);

            await held.LockAccountAsync(user);   // the move holds no account lock while it waits for the personal membership
            await held.ReleaseAsync();
        }

        using var moved = await move;
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        Assert.Equal([acme], await CompaniesOfAsync(user));
        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_join_never_waits_for_a_held_membership_of_another_company()   // lock order; Review Focus 4
    {
        var acme = await CreateCompanyAsync("Acme");
        var user = (await CreateUserAsync(WorkerAddress, confirmed: true)).Id;   // a member of the development company
        var dev = await DevCompanyIdAsync();
        var token = await InvitationTokenAsync(acme, WorkerAddress);

        Task<HttpResponseMessage> acceptance;
        await using (var held = await HeldLock.MembershipRowAsync(Factory, user, dev))   // what a sign-in holds
        {
            acceptance = TenancyApi.AcceptWithoutPassword(Client, token);
            using var accepted = await acceptance.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
            await held.ReleaseAsync();
        }

        Assert.Equal(new[] { dev, acme }.Order(), (await CompaniesOfAsync(user)).Order());
    }
}
