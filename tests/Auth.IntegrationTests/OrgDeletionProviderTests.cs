using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → <c>DELETE /auth/org</c> with a provider ticket (criterion 21; Amendment A12). The answers for a password are spec 0008's.</summary>
public sealed class OrgDeletionProviderTests : ProviderTestBase
{
    private const string Boss = "boss@acme.test";
    private const string BossNormalized = "BOSS@ACME.TEST";
    private const string BossSubject = "100000000000000000055";

    private readonly ShareLockInterceptor _race = new();

    public OrgDeletionProviderTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(_race)));
    }

    private Task<int> CompaniesAsync(Guid company) =>
        InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken));

    private Task<int> UsersAsync() => InDbAsync(db => db.Users.CountAsync(TestContext.Current.CancellationToken));

    private Task<int> AttemptsAsync() =>
        InDbAsync(db => db.LoginStreaks.Where(s => s.IdentifierHash == LoginIdentifier.HashOf(BossNormalized)).Select(s => s.AttemptCount)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken));

    /// <summary>An admin of Acme made through Google: no password, a Google login.</summary>
    private async Task<(Guid Company, Guid AdminId, string Token)> AdminWithoutPasswordAsync()
    {
        var (company, adminId, token) = await CompanyWithAdminAsync("Acme", Boss);
        await InDbAsync(db => db.Users.Where(u => u.Id == adminId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
        await LinkAsync(adminId, "google", BossSubject);
        return (company, adminId, token);
    }

    private Task<ProviderApi.Ticket> ReauthTicketAsync(string query = "?purpose=reauth", string subject = BossSubject, string email = Boss) =>
        TicketAsync("google", GooglePerson(email: email, subject: subject), query);

    [Fact]
    public async Task An_admin_without_a_password_deletes_the_company_with_a_reauth_ticket()   // criterion 21
    {
        var (company, adminId, token) = await AdminWithoutPasswordAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var ticket = await ReauthTicketAsync();

        using var response = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Single(response.Headers.GetValues("Set-Cookie"));   // the one cookie of the answer ...
        ProviderApi.AssertBindingCleared(response);                // ... auth_pt emptied, not the refresh cookie
        Assert.Equal(0, await CompaniesAsync(company));
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == adminId || u.Id == worker, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await TicketCountAsync());
        var reauthenticated = await SingleAsync(AuditKinds.ProviderReauthenticated);
        Assert.Equal((adminId, company), (reauthenticated.ActorUserId!.Value, reauthenticated.OrgId!.Value));
        Assert.Equal(adminId, reauthenticated.SubjectUserId);
        Assert.Equal(Boss, reauthenticated.SubjectEmail);
        Assert.Equal("Acme", reauthenticated.OrgName);
        Assert.Equal(["provider"], AuditApi.DetailNames(reauthenticated));
        Assert.Equal("google", AuditApi.Text(reauthenticated, "provider"));
        var deleted = await SingleAsync(AuditKinds.OrgDeleted);
        Assert.Equal(adminId, deleted.ActorUserId);
        Assert.True(reauthenticated.OccurredAt <= deleted.OccurredAt);
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleteRefused));
    }

    [Fact]
    public async Task A_member_who_keeps_another_space_keeps_the_account_when_the_company_is_deleted_with_a_ticket()   // spec 0015 rule, criterion 21
    {
        var (company, adminId, token) = await AdminWithoutPasswordAsync();
        var other = await CreateCompanyAsync("Globex");
        var keeper = await AddMemberAsync(company, "keeper@acme.test", "user");
        await AddMembershipAsync(keeper, other, "user");
        var ticket = await ReauthTicketAsync();

        using var response = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await CompaniesAsync(company));
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Id == keeper, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == adminId, TestContext.Current.CancellationToken)));   // the admin's last space
        Assert.Equal([other], await InDbAsync(db => db.Memberships.Where(m => m.UserId == keeper).Select(m => m.CompanyId)
            .ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Single(await AuditAsync(AuditKinds.ProviderReauthenticated));
        Assert.Equal("2", AuditApi.Text(await SingleAsync(AuditKinds.OrgDeleted), "members"));
        var accountDeleted = await SingleAsync(AuditKinds.AccountDeleted);   // the admin only, not the keeper
        Assert.Equal(adminId, accountDeleted.SubjectUserId);
        Assert.Equal("org_deleted", AuditApi.Text(accountDeleted, "reason"));
        Assert.DoesNotContain(await AuditAsync(AuditKinds.AccountDeleted), r => r.SubjectUserId == keeper);
    }

    [Fact]
    public async Task Only_accounts_with_no_other_space_are_deleted_with_the_company_and_a_ticket()   // spec 0015 rule, criterion 21
    {
        var (company, adminId, token) = await AdminWithoutPasswordAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var ticket = await ReauthTicketAsync();

        using var response = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == adminId || u.Id == worker, TestContext.Current.CancellationToken)));
        var deleted = await SingleAsync(AuditKinds.OrgDeleted);
        Assert.Equal(["invitations", "members", "roles"], AuditApi.DetailNames(deleted));   // no `keepers` member
        Assert.Equal("2", AuditApi.Text(deleted, "members"));
    }

    [Fact]
    public async Task A_password_is_still_403_reauth_required_and_nothing_is_counted()   // criterion 21
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();

        using var response = await OrgApi.Delete(Client, token, new { name = "Acme", password = "Any-Passw0rd-1" });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_required");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleteRefused));
    }

    [Fact]
    public async Task A_password_for_a_locked_admin_without_a_password_is_403_reauth_required_not_429()   // check order: the fit before the lockout
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 10);
        var attempts = await AttemptsAsync();

        using var response = await OrgApi.Delete(Client, token, new { name = "Acme", password = "Any-Passw0rd-1" });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_required");
        Assert.Equal(attempts, await AttemptsAsync());
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleteRefused));
    }

    [Fact]
    public async Task An_admin_with_a_password_and_a_ticket_is_403_password_required_before_the_lockout()   // criterion 21
    {
        var (company, adminId, token) = await CompanyWithAdminAsync("Acme", Boss);
        await LinkAsync(adminId, "google", BossSubject);
        var ticket = await ReauthTicketAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 10);
        var attempts = await AttemptsAsync();

        using var response = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "password_required");   // not 429: the fit comes first
        Assert.Equal(attempts, await AttemptsAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleteRefused));
        Assert.Null(ProviderApi.BindingCookieOf(response));   // nothing is cleared
    }

    [Theory]
    [InlineData("sign_in")]
    [InlineData("connect")]
    [InlineData("expired")]
    [InlineData("used")]
    [InlineData("unknown")]
    [InlineData("foreign")]
    [InlineData("another-accounts-login")]
    [InlineData("no-binding")]
    [InlineData("another-binding")]
    [InlineData("blank")]
    public async Task A_ticket_that_is_not_a_live_reauth_ticket_of_the_admin_is_403_reauth_failed_and_nothing_is_deleted(string how)   // criterion 21
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        if (how == "another-accounts-login")
        {
            // The identity of the ticket is a login, but of a member's account, not of the caller's.
            var member = await AddMemberAsync(company, "someone@acme.test", "user");
            await LinkAsync(member, "google", "100000000000000000099");
        }

        var ticket = how switch
        {
            "sign_in" => await ReauthTicketAsync(""),
            "connect" => await ReauthTicketAsync("?purpose=connect"),
            "foreign" or "another-accounts-login" => await ReauthTicketAsync(subject: "100000000000000000099", email: "someone@acme.test"),
            _ => await ReauthTicketAsync(),
        };
        var value = how switch
        {
            "blank" => "",
            "unknown" => new string('A', 43),
            _ => ticket.Value,
        };
        string? binding = ticket.Binding;
        switch (how)
        {
            case "expired":
                Clock.Advance(Auth.Server.Providers.ProviderTickets.ReauthLifetime + TimeSpan.FromSeconds(1));
                break;
            case "used":
                await InDbAsync(db => db.ProviderTickets.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
                break;
            case "no-binding":
                binding = null;
                break;
            case "another-binding":
                binding = (await TicketAsync("microsoft", PersonalMicrosoft())).Binding;
                break;
        }

        var tickets = await TicketCountAsync();
        var users = await UsersAsync();
        using var response = await OrgApi.Delete(Client, token, new { name = "Acme", provider_ticket = value }, binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Null(ProviderApi.BindingCookieOf(response));   // auth_pt is not cleared by a refusal
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal(users, await UsersAsync());
        Assert.Equal(tickets, await TicketCountAsync());   // nothing used up
        var refusal = await SingleAsync(AuditKinds.OrgDeleteRefused);
        Assert.Equal("reauth_failed", AuditApi.Text(refusal, "reason"));
        Assert.Equal(company, refusal.OrgId);
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleted));
        Assert.Equal(0, await AttemptsAsync());   // no lockout is consulted or counted
    }

    [Fact]
    public async Task A_bad_ticket_is_refused_before_the_name_is_checked()   // check order: the confirmation before the name
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        var ticket = await ReauthTicketAsync("");   // a sign-in ticket

        using var response = await OrgApi.DeleteWithTicket(Client, token, "acme", ticket);   // and a wrong name

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await CompaniesAsync(company));
    }

    [Fact]
    public async Task A_ticket_gone_when_the_lock_is_taken_is_refused_before_the_name_is_checked()   // check order under the lock: the confirmation before the name
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        var ticket = await ReauthTicketAsync();
        await using var held = await HeldLock.CompanyAsync(Factory, company);
        var pending = OrgApi.DeleteWithTicket(Client, token, "acme", ticket);   // a wrong name
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await InDbAsync(db => db.ProviderTickets.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");   // not 400 invalid_request
        Assert.Equal(1, await CompaniesAsync(company));
    }

    [Fact]
    public async Task A_caller_who_lost_org_delete_while_the_deletion_waited_for_the_company_lock_is_403_permissions_changed_and_the_ticket_stays()   // check order under the lock: the permission before the confirmation
    {
        var (company, adminId, token) = await AdminWithoutPasswordAsync();
        var plain = await RoleIdAsync(company, "user");
        var ticket = await ReauthTicketAsync();
        await using var held = await HeldLock.CompanyAsync(Factory, company);
        var pending = OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);   // passed the permission check of the token's database read
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await SetMemberRoleAsync(adminId, plain);
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Equal(1, await TicketCountAsync());   // not used up
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleteRefused));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleted));
    }

    [Fact]
    public async Task A_ticket_neither_consults_nor_clears_the_lockout()   // spec 0012 → DELETE /auth/org: the lockout is not consulted
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 10);
        var attempts = await AttemptsAsync();
        Assert.True(attempts >= 10);
        var ticket = await ReauthTicketAsync();

        using (var refused = await OrgApi.DeleteWithTicket(Client, token, "acme", ticket))   // a wrong name: refused after the confirmation
        {
            await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.BadRequest, "invalid_request");
        }

        Assert.Equal(attempts, await AttemptsAsync());   // not counted, not cleared
        using var response = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);   // the locked identifier does not stop a ticket

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await CompaniesAsync(company));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("rule1")]
    public async Task A_deletion_refused_after_the_ticket_was_used_up_gives_the_ticket_back(string how)   // Amendment A12: the rollback keeps the ticket
    {
        Guid company;
        string token;
        ProviderApi.Ticket ticket;
        if (how == "name")
        {
            (company, _, token) = await AdminWithoutPasswordAsync();
            ticket = await ReauthTicketAsync();
        }
        else
        {
            // Rule 1, as OrgDeleteEndpointTests sets it up: the admin holds *, and a member who holds org:delete only deletes; this one has no password.
            (company, _, _) = await CompanyWithAdminAsync("Acme", Boss);
            await AddRoleAsync(company, "Deleter", "org:delete");
            var deleter = await AddMemberAsync(company, "deleter@acme.test", "Deleter");
            token = (await SessionApi.LoginAsync(Client, "deleter@acme.test", UserPassword)).AccessToken;
            await InDbAsync(db => db.Users.Where(u => u.Id == deleter)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
            await LinkAsync(deleter, "google", "100000000000000000044");
            ticket = await ReauthTicketAsync(subject: "100000000000000000044", email: "deleter@acme.test");
        }

        var users = await UsersAsync();
        using var response = await OrgApi.DeleteWithTicket(Client, token, how == "name" ? "acme" : "Acme", ticket);

        if (how == "name")
        {
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }
        else
        {
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permission_not_held");
        }

        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Equal(1, await TicketCountAsync());   // used up inside the deletion's transaction, which was rolled back
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal(users, await UsersAsync());
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleted));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleteRefused));

        // The ticket still works: the same request with what was wrong put right.
        if (how == "name")
        {
            using var again = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
            Assert.Equal(0, await CompaniesAsync(company));
        }
    }

    [Fact]
    public async Task Two_deletions_with_one_ticket_delete_once()   // criterion 21, with parallel requests
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        var ticket = await ReauthTicketAsync();

        Task<HttpResponseMessage>[] pending;
        await using (var held = await HeldLock.CompanyAsync(Factory, company))
        {
            pending = [OrgApi.DeleteWithTicket(Client, token, "Acme", ticket), OrgApi.DeleteWithTicket(Client, token, "Acme", ticket)];
            await HeldLock.WaitForWaitersAsync(Factory, 2, pending);
            await held.ReleaseAsync();
        }

        var answers = await Task.WhenAll(pending);
        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Forbidden], answers.Select(a => a.StatusCode).Order());
        // The loser finds the company gone under the lock: the token no longer matches the database, as for two deletions with a password.
        var loser = answers.Single(a => a.StatusCode == HttpStatusCode.Forbidden);
        await TenancyApi.AssertErrorAsync(loser, HttpStatusCode.Forbidden, "permissions_changed");
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleteRefused));
        Assert.Single(await AuditAsync(AuditKinds.OrgDeleted));
        Assert.Single(await AuditAsync(AuditKinds.ProviderReauthenticated));
        Assert.Equal(0, await CompaniesAsync(company));
        foreach (var answer in answers)
        {
            answer.Dispose();
        }
    }

    [Fact]
    public async Task A_ticket_used_up_by_the_confirmation_while_the_deletion_waited_for_the_company_lock_deletes_nothing()   // criterion 21: one ticket, one use, inside the deletion's transaction
    {
        var (company, adminId, token) = await CompanyWithAdminAsync("Acme", Boss);
        var session = await SessionApi.LoginAsync(Client, Boss, UserPassword);   // the confirmation needs the refresh cookie
        await InDbAsync(db => db.Users.Where(u => u.Id == adminId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
        await LinkAsync(adminId, "google", BossSubject);
        var ticket = await ReauthTicketAsync();
        await using var held = await HeldLock.CompanyAsync(Factory, company);
        var pending = OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);   // passed its checks, waits for the company lock before it uses the ticket
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);

        using (var confirmed = await SettingsApi.ConfirmWithTicket(Client, session, ticket))   // the other consumer of the same ticket takes it first
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        await held.ReleaseAsync();
        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal(0, await TicketCountAsync());   // used once, by the confirmation
        Assert.Single(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleted));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.OrgDeleteRefused), "reason"));
    }

    [Fact]
    public async Task A_ticket_used_up_while_the_deletion_waited_for_the_company_lock_is_403_reauth_failed()   // guard lesson 1
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        var ticket = await ReauthTicketAsync();
        await using var held = await HeldLock.CompanyAsync(Factory, company);
        var pending = OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await InDbAsync(db => db.ProviderTickets.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.OrgDeleteRefused), "reason"));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
        Assert.Null(ProviderApi.BindingCookieOf(response));
    }

    [Fact]
    public async Task A_ticket_that_expired_while_the_deletion_waited_is_refused()   // guard lesson 11: the clock after the locks
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        var ticket = await ReauthTicketAsync();
        await using var held = await HeldLock.CompanyAsync(Factory, company);
        var pending = OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        Clock.Advance(TimeSpan.FromMinutes(5));
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal(1, await TicketCountAsync());   // expired, not used up
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.OrgDeleteRefused), "reason"));
    }

    [Fact]
    public async Task A_login_unlinked_while_the_deletion_waited_for_the_company_lock_is_403_reauth_failed_and_the_ticket_stays()   // guard lesson 1: "linked meanwhile" the other way
    {
        var (company, adminId, token) = await AdminWithoutPasswordAsync();
        var ticket = await ReauthTicketAsync();
        await using var held = await HeldLock.CompanyAsync(Factory, company);
        var pending = OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await InDbAsync(db => db.UserLogins.Where(l => l.UserId == adminId).ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal(1, await TicketCountAsync());   // the rollback gave it back
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.OrgDeleteRefused), "reason"));
    }

    [Fact]
    public async Task A_member_unlinking_a_login_while_the_deletion_with_a_ticket_runs_neither_deadlocks_nor_loses_the_deletion()   // lock order: the account's row before the login rows
    {
        var (company, admin, token) = await AdminWithoutPasswordAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        await LinkAsync(worker, "google", "100000000000000000077");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        var ticket = await ReauthTicketAsync();

        // The unlink reads the account's row FOR KEY SHARE twice: early, which holds the row, and again at its commit (Skip = 1 lets the first
        // pass). Stopped before the second read, it has deleted the login and revoked the sessions and still holds the row: the deletion has
        // used its ticket up and must queue behind that share lock, as a member removal does.
        Task<HttpResponseMessage>? deletion = null;
        _race.Pattern = "FOR KEY SHARE";
        _race.Skip = 1;
        _race.Before = async () =>
        {
            deletion = OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);
            await HeldLock.WaitForWaitersAsync(Factory, 1, deletion);
        };

        using var unlinked = await SettingsApi.Unlink(Client, session, "google");

        Assert.True(_race.Fired);
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        using var deleted = await deletion!;
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await CompaniesAsync(company));
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == admin || u.Id == worker, TestContext.Current.CancellationToken)));
    }

    public static TheoryData<string> BadBodies => new()
    {
        """{"name":"Acme","password":"Another-Passw0rd","provider_ticket":"x"}""",
        """{"name":"Acme","provider_ticket":5}""",
        """{"name":"Acme","provider_ticket":null}""",
        """{"name":"Acme"}""",
        """{"provider_ticket":"x"}""",
        """{"name":"  ","provider_ticket":"x"}""",
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task Both_members_neither_or_one_of_the_wrong_type_is_400_invalid_request(string body)   // criterion 21
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();

        using var request = new HttpRequestMessage(HttpMethod.Delete, OrgApi.Path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");   // before the fit: this admin has no password
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleteRefused));
    }

    [Fact]
    public async Task No_ticket_binding_or_token_reaches_the_audit_log_or_the_log()   // guard lesson 4
    {
        var (_, _, token) = await AdminWithoutPasswordAsync();
        var ticket = await ReauthTicketAsync();
        string refusedBody;
        using (var refused = await OrgApi.DeleteWithTicket(Client, token, "acme", ticket))   // refused after the ticket was used up, and rolled back
        {
            refusedBody = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }

        using (var response = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        string[] secrets = [ticket.Value, ticket.Binding, token];
        await AssertNoSecretsAsync(secrets);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal));
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, refusedBody, StringComparison.Ordinal));
    }
}
