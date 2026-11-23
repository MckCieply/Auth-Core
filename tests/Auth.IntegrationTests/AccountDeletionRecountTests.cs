using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0017's recount, added by spec 0016: for a removal and a company deletion the routine counts the memberships of the accounts again,
/// under their row locks, and keeps an account that has one (it may have gained one since the caller looked).
/// </summary>
public sealed class AccountDeletionRecountTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private async Task<int> DeleteAsync(IReadOnlyCollection<Guid> users, AccountDeletionReason reason)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var deleted = await scope.ServiceProvider.GetRequiredService<AccountDeletion>()
            .DeleteAsync(users, reason, actor: null, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return deleted;
    }

    private Task<int> UsersAsync(Guid id) => InDbAsync(db => db.Users.CountAsync(u => u.Id == id, TestContext.Current.CancellationToken));

    private Task<int> MembershipsAsync(Guid id) => InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == id, TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_member_removed_account_that_still_has_a_membership_is_kept()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var a = await CreateCompanyAsync("Acme");
        var b = await CreateCompanyAsync("Globex");
        var worker = await AddMemberAsync(a, "worker@acme.test", "user");
        await AddMembershipAsync(worker, b, "user");
        _ = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);   // a refresh token and an authorization
        using (var wrong = await LoginApi.Login(Client, "worker@acme.test", "Wrong-Password-1"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);              // a login streak
        }

        var subject = worker.ToString();
        var streak = LoginIdentifier.HashOf("WORKER@ACME.TEST");
        var tokensBefore = await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync(t => t.Subject == subject, cancellation));
        Assert.True(tokensBefore > 0);
        Assert.Equal(1, await InDbAsync(db => db.LoginStreaks.CountAsync(s => s.IdentifierHash == streak, cancellation)));

        Assert.Equal(0, await DeleteAsync([worker], AccountDeletionReason.MemberRemoved));

        Assert.Equal(1, await UsersAsync(worker));
        Assert.Equal(2, await MembershipsAsync(worker));
        Assert.Equal(tokensBefore, await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync(t => t.Subject == subject, cancellation)));
        Assert.Equal(1, await InDbAsync(db => db.LoginStreaks.CountAsync(s => s.IdentifierHash == streak, cancellation)));
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleted));
    }

    [Fact]
    public async Task An_org_deleted_batch_keeps_only_the_accounts_that_have_a_membership()
    {
        var a = await CreateCompanyAsync("Acme");
        var kept = await AddMemberAsync(a, "kept@acme.test", "user");
        var gone = await AddMemberAsync(a, "gone@acme.test", "user");
        await RemoveMembershipAsync(gone);   // the caller removed its memberships first, as the real callers do

        Assert.Equal(1, await DeleteAsync([kept, gone], AccountDeletionReason.OrgDeleted));

        Assert.Equal(1, await UsersAsync(kept));
        Assert.Equal(0, await UsersAsync(gone));
        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(gone, row.SubjectUserId);
    }

    [Fact]
    public async Task A_self_deletion_is_not_recounted()
    {
        var (user, space) = await CreatePrivateAccountAsync("ola@poczta.test");

        Assert.Equal(1, await DeleteAsync([user], AccountDeletionReason.Self));

        Assert.Equal(0, await UsersAsync(user));
        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, TestContext.Current.CancellationToken)));
        _ = await SingleAsync(AuditKinds.AccountDeleted);
    }

    [Fact]
    public async Task Removing_the_last_membership_still_deletes_the_account_and_reports_it()
    {
        var (company, _, _) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var other = await CreateCompanyAsync("Globex");
        var only = await AddMemberAsync(company, "only@acme.test", "user");
        var two = await AddMemberAsync(company, "two@acme.test", "user");
        await AddMembershipAsync(two, other, "user");

        var removed = await RemoveAsync(company, only);
        Assert.True(removed.AccountDeleted);
        Assert.Equal(0, await UsersAsync(only));
        Assert.Equal(only, (await SingleAsync(AuditKinds.AccountDeleted)).SubjectUserId);

        var kept = await RemoveAsync(company, two);
        Assert.False(kept.AccountDeleted);
        Assert.Equal(1, await UsersAsync(two));
        Assert.Equal(1, await MembershipsAsync(two));
        _ = Assert.Single(await AuditAsync(AuditKinds.AccountDeleted));   // still only the first
        Assert.Equal(2, (await AuditAsync(AuditKinds.MemberRemoved)).Count);
    }

    private async Task<MemberRemoval> RemoveAsync(Guid company, Guid user)
    {
        using var scope = Factory.Services.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<MemberService>()
            .RemoveAsync(Actor.Operator, company, user, force: true, TestContext.Current.CancellationToken);
        Assert.True(outcome.Succeeded, outcome.Error);
        return outcome.Value!;
    }

    [Fact]
    public async Task A_membership_inserted_while_the_routine_waits_keeps_the_account()
    {
        var (a, admin, token) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var b = await CreateCompanyAsync("Globex");
        var worker = await AddMemberAsync(a, "worker@acme.test", "user");
        var roleInB = await RoleIdAsync(b, "user");

        Task<HttpResponseMessage> removal;
        await using (var held = await HeldLock.AccountRowAsync(Factory, worker))
        {
            removal = SendFromCallerAsync(HttpMethod.Delete, $"/auth/org/members/{worker}", token);
            await HeldLock.WaitForWaitersAsync(Factory, 1, removal);   // the removal has deleted the membership and waits for the account row

            await held.InsertMembershipAsync(worker, b, roleInB);      // a creation of a company would leave this
            await held.ReleaseAsync();
        }

        using (var response = await removal)
        {
            await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        }

        Assert.Equal(1, await UsersAsync(worker));
        Assert.Equal([b], await InDbAsync(db => db.Memberships.Where(m => m.UserId == worker).Select(m => m.CompanyId)
            .ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleted));
        var row = await SingleAsync(AuditKinds.MemberRemoved);
        Assert.Equal(worker, row.SubjectUserId);
        Assert.Equal(admin, row.ActorUserId);
    }
}
