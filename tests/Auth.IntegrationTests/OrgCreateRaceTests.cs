using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0016, criteria 10 and 18: parallel creations at the limit, and a creation beside a self-deletion or the removal of the account's last
/// membership. Each race that must run in a named order holds a lock of the test and lets the requests queue behind it, as
/// <c>PrivateAccountRaceTests</c> does; no test sleeps.
/// </summary>
public sealed class OrgCreateRaceTests : AuditTestBase
{
    private const string Solo = "solo@example.test";
    private const string SoloNormalized = "SOLO@EXAMPLE.TEST";

    public OrgCreateRaceTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithOrgCreation().WithSetting(CompanyLimitSettings.Key, "2");
    }

    [Fact]
    public async Task Parallel_creations_at_the_limit_minus_one_make_exactly_one_company()   // criterion 10
    {
        var (_, admin, _) = await CompanyWithAdminAsync();
        var session = await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword);
        Assert.Equal(1, await CompanyCountOfAsync(admin));

        var before = (await AuditAsync(AuditKinds.OrgCreated)).Count;

        var requests = Enumerable.Range(0, 5)
            .Select(i => TenancyApi.CreateOrg(Client, session.AccessToken, new { name = $"Beta {i}" }))
            .ToList();
        var answers = await Task.WhenAll(requests);
        try
        {
            Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.Created));
            foreach (var refused in answers.Where(a => a.StatusCode != HttpStatusCode.Created))
            {
                await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.Conflict, "company_limit_reached");
            }
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }

        Assert.Equal(2, await CompanyCountOfAsync(admin));
        Assert.Equal(before + 1, (await AuditAsync(AuditKinds.OrgCreated)).Count);
    }

    private async Task<(Task<HttpResponseMessage> Creation, Task<HttpResponseMessage> Deletion)> RaceCreationAndSelfDeletionAsync(bool creationFirst)
    {
        await CreatePrivateAccountAsync(Solo);
        var session = await SessionApi.LoginAsync(Client, Solo, UserPassword);

        Task<HttpResponseMessage> creation;
        Task<HttpResponseMessage> deletion;
        await using (var held = await HeldLock.AddressAsync(Factory, SoloNormalized))
        {
            if (creationFirst)
            {
                creation = TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Solo Ltd" });
                await HeldLock.WaitForWaitersAsync(Factory, 1, creation);
                deletion = TenancyApi.Send(Client, HttpMethod.Delete, "/auth/account", session.AccessToken, new { password = UserPassword });
                await HeldLock.WaitForWaitersAsync(Factory, 2, creation, deletion);
            }
            else
            {
                deletion = TenancyApi.Send(Client, HttpMethod.Delete, "/auth/account", session.AccessToken, new { password = UserPassword });
                await HeldLock.WaitForWaitersAsync(Factory, 1, deletion);
                creation = TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Solo Ltd" });
                await HeldLock.WaitForWaitersAsync(Factory, 2, deletion, creation);
            }

            await held.ReleaseAsync();
        }

        return (creation, deletion);
    }

    private async Task AssertCreationAndSelfDeletionConsistentAsync(Task<HttpResponseMessage> creationTask, Task<HttpResponseMessage> deletionTask)
    {
        using var creation = await creationTask;
        using var deletion = await deletionTask;
        var accounts = await InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == SoloNormalized, TestContext.Current.CancellationToken));
        var spaces = await InDbAsync(db => db.Companies.CountAsync(c => c.Kind == CompanyKind.Personal, TestContext.Current.CancellationToken));
        var companies = await InDbAsync(db => db.Companies.CountAsync(c => c.Kind == CompanyKind.Organization && c.Name == "Solo Ltd", TestContext.Current.CancellationToken));

        if (creation.StatusCode == HttpStatusCode.Created)
        {
            // 403 either way: today `forbidden`, `company_member` once Task 9 of plan 0016 lands.
            Assert.Equal(HttpStatusCode.Forbidden, deletion.StatusCode);
            Assert.Equal((1, 1, 1), (accounts, spaces, companies));
        }
        else
        {
            await TenancyApi.AssertErrorAsync(creation, HttpStatusCode.Forbidden, "permissions_changed");
            Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
            Assert.Equal((0, 0, 0), (accounts, spaces, companies));
        }

        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(
            c => !db.Memberships.Any(m => m.CompanyId == c.Id), TestContext.Current.CancellationToken)));   // no company without a member
    }

    [Fact]
    public async Task A_creation_and_a_self_deletion_of_one_private_account_never_leave_a_company_without_a_member()   // criterion 18, creation first
    {
        var (creation, deletion) = await RaceCreationAndSelfDeletionAsync(creationFirst: true);
        await AssertCreationAndSelfDeletionConsistentAsync(creation, deletion);
    }

    [Fact]
    public async Task A_self_deletion_and_a_creation_of_one_private_account_never_leave_a_company_without_a_member()   // criterion 18, deletion first
    {
        var (creation, deletion) = await RaceCreationAndSelfDeletionAsync(creationFirst: false);
        await AssertCreationAndSelfDeletionConsistentAsync(creation, deletion);
    }

    private async Task AssertCreationAndRemovalConsistentAsync(bool creationFirst)
    {
        var (acme, _, adminToken) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(acme, "worker@acme.test", "user");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);

        Task<HttpResponseMessage> creation;
        Task<HttpResponseMessage> removal;
        await using (var held = await HeldLock.AccountRowAsync(Factory, worker))
        {
            if (creationFirst)
            {
                creation = TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Worker Ltd" });
                await HeldLock.WaitForWaitersAsync(Factory, 1, creation);
                removal = TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", adminToken);
                await HeldLock.WaitForWaitersAsync(Factory, 2, creation, removal);
            }
            else
            {
                removal = TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", adminToken);
                await HeldLock.WaitForWaitersAsync(Factory, 1, removal);
                creation = TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Worker Ltd" });
                await HeldLock.WaitForWaitersAsync(Factory, 2, removal, creation);
            }

            await held.ReleaseAsync();
        }

        using var created = await creation;
        using var removed = await removal;
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var account = await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken));
        var memberships = await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == worker, TestContext.Current.CancellationToken));
        var deletedRows = (await AuditAsync(AuditKinds.AccountDeleted)).Count;

        if (created.StatusCode == HttpStatusCode.Created)
        {
            Assert.Equal((1, 1, 0), (account, memberships, deletedRows));   // the account is kept with the new company
        }
        else
        {
            await TenancyApi.AssertErrorAsync(created, HttpStatusCode.Forbidden, "permissions_changed");
            Assert.Equal((0, 0, 1), (account, memberships, deletedRows));   // the removal came first and deleted the account
        }
    }

    [Fact]
    public async Task A_creation_and_the_removal_of_the_accounts_last_company_membership_never_500()   // criterion 18, creation first
    {
        await AssertCreationAndRemovalConsistentAsync(creationFirst: true);
    }

    [Fact]
    public async Task The_removal_of_the_accounts_last_company_membership_and_a_creation_never_500()   // criterion 18, removal first
    {
        await AssertCreationAndRemovalConsistentAsync(creationFirst: false);
    }
}
