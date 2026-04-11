using System.Data.Common;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → What an account deletion removes: the one routine, called here straight, in a transaction of the test.</summary>
public sealed class AccountDeletionTests : AuditTestBase
{
    private const string Private = "ola@poczta.test";
    private const string Bystander = "bea@poczta.test";
    private const string Stranger = "stranger@poczta.test";

    private readonly StatementInterceptor _statements = new();

    public AccountDeletionTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(_statements)));
    }

    private async Task<int> DeleteAsync(IReadOnlyCollection<Guid> users, AccountDeletionReason reason, Actor? actor)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var deleted = await scope.ServiceProvider.GetRequiredService<AccountDeletion>().DeleteAsync(users, reason, actor, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return deleted;
    }

    private Task<int> CountAsync(Func<AuthDbContext, Task<int>> count) => InDbAsync(count);

    /// <summary>Every kind of row that belongs to an account, counted for one account, its space and its address.</summary>
    private sealed record Rows(
        int Users, int Memberships, int Spaces, int Roles, int Tokens, int Authorizations, int Logins, int Claims, int UserTokens,
        int EmailTokens, int Mails, int Signups, int Streaks);

    private Task<Rows> RowsOfAsync(Guid user, Guid space, string address)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var subject = user.ToString();
        var normalized = address.ToUpperInvariant();
        var streak = LoginIdentifier.HashOf(normalized);
        return InDbAsync(async db => new Rows(
            Users: await db.Users.CountAsync(u => u.Id == user, cancellation),
            Memberships: await db.Memberships.CountAsync(m => m.UserId == user, cancellation),
            Spaces: await db.Companies.CountAsync(c => c.Id == space, cancellation),
            Roles: await db.CompanyRoles.CountAsync(r => r.CompanyId == space, cancellation),
            Tokens: await db.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync(t => t.Subject == subject, cancellation),
            Authorizations: await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().CountAsync(a => a.Subject == subject, cancellation),
            Logins: await db.UserLogins.CountAsync(l => l.UserId == user, cancellation),
            Claims: await db.UserClaims.CountAsync(c => c.UserId == user, cancellation),
            UserTokens: await db.UserTokens.CountAsync(t => t.UserId == user, cancellation),
            EmailTokens: await db.EmailTokens.CountAsync(t => t.UserId == user, cancellation),
            Mails: await db.MailRequests.CountAsync(r => r.NormalizedEmail == normalized && r.Kind != MailKind.Invitation, cancellation),
            Signups: await db.PendingSignups.CountAsync(p => p.NormalizedEmail == normalized, cancellation),
            Streaks: await db.LoginStreaks.CountAsync(s => s.IdentifierHash == streak, cancellation)));
    }

    private static void AssertEveryKindIsPresent(Rows rows)
    {
        Assert.Equal(1, rows.Users);
        Assert.Equal(1, rows.Memberships);
        Assert.Equal(1, rows.Spaces);
        Assert.Equal(1, rows.Roles);
        Assert.True(rows.Tokens > 0);
        Assert.True(rows.Authorizations > 0);
        Assert.True(rows.Logins > 0);
        Assert.True(rows.Claims > 0);
        Assert.True(rows.UserTokens > 0);
        Assert.True(rows.EmailTokens > 0);
        Assert.True(rows.Mails > 0);
        Assert.True(rows.Signups > 0);
        Assert.True(rows.Streaks > 0);
    }

    private async Task AddIdentityRowsAsync(Guid user)
    {
        var cancellation = TestContext.Current.CancellationToken;
        await InDbAsync(async db =>
        {
            db.UserLogins.Add(new IdentityUserLogin<Guid> { LoginProvider = "test", ProviderKey = "key", ProviderDisplayName = "Test", UserId = user });
            db.UserClaims.Add(new IdentityUserClaim<Guid> { UserId = user, ClaimType = "test", ClaimValue = "value" });
            db.UserTokens.Add(new IdentityUserToken<Guid> { UserId = user, LoginProvider = "test", Name = "test", Value = "value" });
            return await db.SaveChangesAsync(cancellation);
        });
    }

    /// <summary>The statements the routine sends for these accounts, in a transaction of the test.</summary>
    private async Task<int> CountStatementsAsync(IReadOnlyCollection<Guid> users)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        _statements.Watch(db);
        int sent;
        try
        {
            Assert.Equal(users.Count, await scope.ServiceProvider.GetRequiredService<AccountDeletion>()
                .DeleteAsync(users, AccountDeletionReason.OrgDeleted, Actor.Operator, TestContext.Current.CancellationToken));
        }
        finally
        {
            sent = _statements.Stop();
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return sent;
    }

    [Fact]
    public async Task Deleting_a_private_account_removes_everything_of_it_and_keeps_its_audit_rows_invitations_and_mail_limits()   // criterion 21
    {
        var cancellation = TestContext.Current.CancellationToken;
        var (user, space) = await CreatePrivateAccountAsync(Private);
        _ = await SessionApi.LoginAsync(Client, Private, UserPassword);
        _ = await SessionApi.LoginAsync(Client, Private, UserPassword);   // two sessions: tokens and authorizations
        await EnqueueAsync(MailKind.PasswordReset, Private);
        await DispatchAsync();                                              // a mail link (EmailTokens)
        await EnqueueAsync(MailKind.EmailVerification, Private);           // a queued request of the address, not an invitation
        var acme = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(acme, Private, "user");
        await EnqueueInvitationAsync(invite, Private);                     // an invitation to the address and its queued mail
        await PendingSignupSeed.IssueAsync(Factory.Services, Private, "Firma");
        using (var wrong = await LoginApi.Login(Client, Private, WrongPassword))
        {
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, wrong.StatusCode);   // a login streak
        }

        await AddIdentityRowsAsync(user);

        // Another private account, with a row of every kind the routine deletes by address, subject or id, and a pending sign-up of a stranger.
        var (bystander, bystanderSpace) = await CreatePrivateAccountAsync(Bystander);
        _ = await SessionApi.LoginAsync(Client, Bystander, UserPassword);
        await EnqueueAsync(MailKind.EmailVerification, Bystander);
        using (var wrongBystander = await LoginApi.Login(Client, Bystander, WrongPassword))
        {
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, wrongBystander.StatusCode);
        }

        await PendingSignupSeed.IssueAsync(Factory.Services, Stranger, "Firma");
        var strangerSignup = Stranger.ToUpperInvariant();

        var rowsBefore = await RowsOfAsync(user, space, Private);
        AssertEveryKindIsPresent(rowsBefore);
        var bystanderBefore = await RowsOfAsync(bystander, bystanderSpace, Bystander);
        Assert.Equal(1, bystanderBefore.Users);
        Assert.Equal(1, bystanderBefore.Memberships);
        Assert.Equal(1, bystanderBefore.Spaces);
        Assert.Equal(1, bystanderBefore.Roles);
        Assert.True(bystanderBefore.Tokens > 0);
        Assert.True(bystanderBefore.Authorizations > 0);
        Assert.Equal(1, bystanderBefore.Mails);
        Assert.Equal(1, bystanderBefore.Streaks);
        Assert.Equal(1, await CountAsync(db => db.PendingSignups.CountAsync(p => p.NormalizedEmail == strangerSignup, cancellation)));
        Assert.Equal(1, await CountAsync(db => db.Invites.CountAsync(i => i.Id == invite, cancellation)));
        Assert.Equal(1, await CountAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.Invitation, cancellation)));
        var limits = await CountAsync(db => db.MailRequestLimits.CountAsync(cancellation));
        Assert.True(limits > 0);
        var auditBefore = await CountAsync(db => db.AuditEvents.CountAsync(a => a.SubjectUserId == user, cancellation));

        Assert.Equal(1, await DeleteAsync([user], AccountDeletionReason.Self, actor: null));

        Assert.Equal(new Rows(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), await RowsOfAsync(user, space, Private));

        // Not touched: the other private account with its space and its role row, its sessions, its mail and its streak, and the pending sign-up of the stranger.
        Assert.Equal(bystanderBefore, await RowsOfAsync(bystander, bystanderSpace, Bystander));
        Assert.Equal(1, await CountAsync(db => db.PendingSignups.CountAsync(p => p.NormalizedEmail == strangerSignup, cancellation)));

        // Kept: the invitation of a company to the address and its mail, the mail limits, and every audit row that names the account.
        Assert.Equal(1, await CountAsync(db => db.Invites.CountAsync(i => i.Id == invite, cancellation)));
        Assert.Equal(1, await CountAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.Invitation, cancellation)));
        Assert.Equal(limits, await CountAsync(db => db.MailRequestLimits.CountAsync(cancellation)));
        Assert.Equal(auditBefore + 1, await CountAsync(db => db.AuditEvents.CountAsync(a => a.SubjectUserId == user, cancellation)));

        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(user, row.ActorUserId);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal(Private, row.SubjectEmail);
        Assert.Equal(space, row.OrgId);
        Assert.Equal("private", row.OrgName);
        Assert.Equal("self", AuditApi.Text(row, "reason"));
        Assert.Equal(["reason"], AuditApi.DetailNames(row));
        Assert.Null(row.ClientIp);   // no request: the test called the routine
    }

    [Fact]
    public async Task Deleting_a_company_member_keeps_the_company_and_its_invitations_lose_their_sender()
    {
        var (company, admin, _) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var worker = await AddMemberAsync(company, "worker@acme.test", "admin");
        var invite = await AddInviteAsync(company, "new@acme.test", "user", invitedBy: worker);

        Assert.Equal(1, await DeleteAsync([worker], AccountDeletionReason.MemberRemoved, Actor.Of(await TenantOfAsync(admin))));

        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken)));
        Assert.Null((await InviteAsync(invite))!.InvitedBy);
        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(worker, row.SubjectUserId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal("member_removed", AuditApi.Text(row, "reason"));
    }

    [Fact]
    public async Task The_operator_is_marked_cli_and_the_start_up_step_has_no_actor_and_no_space()
    {
        var (company, _, _) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var member = await AddMemberAsync(company, "worker@acme.test", "user");
        var lonely = (await CreateUserAsync("lonely@example.test", confirmed: true, member: false)).Id;

        await DeleteAsync([member], AccountDeletionReason.OrgDeleted, Actor.Operator);
        await DeleteAsync([lonely], AccountDeletionReason.Upgrade, actor: null);

        var rows = await AuditAsync(AuditKinds.AccountDeleted);
        var byOperator = Assert.Single(rows, r => r.SubjectUserId == member);
        Assert.Null(byOperator.ActorUserId);
        Assert.Equal("cli", AuditApi.Text(byOperator, "via"));
        Assert.Equal("org_deleted", AuditApi.Text(byOperator, "reason"));
        var upgrade = Assert.Single(rows, r => r.SubjectUserId == lonely);
        Assert.Null(upgrade.ActorUserId);
        Assert.Null(upgrade.OrgId);
        Assert.Null(upgrade.OrgName);
        Assert.Equal(["reason"], AuditApi.DetailNames(upgrade));
        Assert.Equal("upgrade", AuditApi.Text(upgrade, "reason"));
    }

    [Fact]
    public async Task An_account_that_is_already_gone_is_skipped_and_writes_nothing()
    {
        Assert.Equal(0, await DeleteAsync([Guid.NewGuid()], AccountDeletionReason.Self, actor: null));

        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleted));
    }

    [Fact]
    public async Task A_mix_of_an_account_one_already_gone_and_a_repeated_id_deletes_the_account_once()
    {
        var (user, _) = await CreatePrivateAccountAsync(Private);
        var (other, _) = await CreatePrivateAccountAsync(Bystander);

        Assert.Equal(1, await DeleteAsync([user, Guid.NewGuid(), user], AccountDeletionReason.Self, actor: null));

        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == user, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Id == other, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Without_a_transaction_the_routine_refuses_and_deletes_nothing()
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        using var scope = Factory.Services.CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<AccountDeletion>()
            .DeleteAsync([user], AccountDeletionReason.Self, actor: null, TestContext.Current.CancellationToken));

        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Id == user, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task An_account_that_goes_away_between_the_read_and_the_delete_aborts_the_deletion()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var (user, space) = await CreatePrivateAccountAsync(Private);
        await PendingSignupSeed.IssueAsync(Factory.Services, Private, "Firma");
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation);
        _statements.Watch(db);

        // Another request deletes the account just before the routine deletes it.
        _statements.BeforeUserDelete = () => InDbAsync(other => other.Users.Where(u => u.Id == user).ExecuteDeleteAsync(cancellation));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<AccountDeletion>()
                .DeleteAsync([user], AccountDeletionReason.Self, actor: null, cancellation));
        }
        finally
        {
            _ = _statements.Stop();
        }

        await transaction.RollbackAsync(cancellation);

        var normalized = Private.ToUpperInvariant();
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleted));
        Assert.Equal(1, await CountAsync(d => d.PendingSignups.CountAsync(p => p.NormalizedEmail == normalized, cancellation)));
        Assert.Equal(1, await CountAsync(d => d.Companies.CountAsync(c => c.Id == space, cancellation)));
    }

    [Fact]
    public async Task A_company_of_any_size_costs_the_same_number_of_statements()
    {
        var one = await CountStatementsAsync([(await CreatePrivateAccountAsync("one@poczta.test")).UserId]);
        var three = await CountStatementsAsync(
        [
            (await CreatePrivateAccountAsync("two@poczta.test")).UserId,
            (await CreatePrivateAccountAsync("three@poczta.test")).UserId,
            (await CreatePrivateAccountAsync("four@poczta.test")).UserId,
        ]);

        Assert.Equal(one, three);
        Assert.InRange(one, 2, 9);   // two reads and at most seven deletes
    }

    [Fact]
    public async Task Many_accounts_at_once_get_one_row_each_and_leave_the_others()
    {
        var (company, admin, _) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var members = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            members.Add(await AddMemberAsync(company, $"worker{i}@acme.test", "user"));
        }

        Assert.Equal(3, await DeleteAsync(members, AccountDeletionReason.OrgDeleted, Actor.Of(await TenantOfAsync(admin))));

        Assert.Equal(3, (await AuditAsync(AuditKinds.AccountDeleted)).Count);
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Id == admin, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == company, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData(AccountDeletionReason.Self, "self")]
    [InlineData(AccountDeletionReason.MemberRemoved, "member_removed")]
    [InlineData(AccountDeletionReason.OrgDeleted, "org_deleted")]
    [InlineData(AccountDeletionReason.Upgrade, "upgrade")]
    public void The_reasons_have_the_names_of_the_contract(AccountDeletionReason reason, string name)   // spec 0010 → Audit events
    {
        Assert.Equal(name, AccountDeletion.NameOf(reason));
    }

    /// <summary>
    /// Counts the commands one context sends while it is watched, and can run a step just before the account delete is sent (to let another
    /// request act at that moment). Commands of other contexts are neither counted nor delayed.
    /// </summary>
    private sealed class StatementInterceptor : DbCommandInterceptor
    {
        private DbContext? _watched;
        private int _count;

        public Func<Task>? BeforeUserDelete { get; set; }

        public void Watch(DbContext context)
        {
            _count = 0;
            _watched = context;
        }

        public int Stop()
        {
            _watched = null;
            BeforeUserDelete = null;
            return _count;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await ObserveAsync(command, eventData);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await ObserveAsync(command, eventData);
            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            await ObserveAsync(command, eventData);
            return result;
        }

        private async Task ObserveAsync(DbCommand command, CommandEventData eventData)
        {
            if (!ReferenceEquals(eventData.Context, _watched))
            {
                return;
            }

            _count++;
            var step = BeforeUserDelete;
            if (step is not null && command.CommandText.Contains("DELETE FROM \"AspNetUsers\"", StringComparison.Ordinal))
            {
                BeforeUserDelete = null;
                await step();
            }
        }
    }
}
