using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class SignupCompleteTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    // Spelled out, so that the escape written into a request body is not read as an escape of this file.
    private const string Backslash = "\\";
    private const string WeakRules = """{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}""";

    public static TheoryData<string> MalformedBodies => new()
    {
        """{"token":"TOKEN","password":"Chosen-Passw0rd"}""",
        """{"token":"TOKEN","company_name":"Firma"}""",
        """{"password":"Chosen-Passw0rd","company_name":"Firma"}""",
        """{"token":"TOKEN","password":"Chosen-Passw0rd","company_name":""}""",
        """{"token":"TOKEN","password":"Chosen-Passw0rd","company_name":"   "}""",
        """{"token":"TOKEN","password":"","company_name":"Firma"}""",
        """{"token":"TOKEN","password":123,"company_name":"Firma"}""",
        """{"token":"TOKEN","password":"Chosen-Passw0rd","company_name":null}""",
        """{"token":"TOKEN","password":"Chosen-Passw0rd","company_name":" Firma"}""",
        """{"token":"TOKEN","password":"Chosen-Passw0rd","company_name":"Firma "}""",
        "{\"token\":\"TOKEN\",\"password\":\"Chosen-Passw0rd\",\"company_name\":\"" + new string('x', 101) + "\"}",
        "{\"token\":\"TOKEN\",\"password\":\"Chosen" + Backslash + "u0000Passw0rd\",\"company_name\":\"Firma\"}",   // NUL in the password
        "{\"token\":\"TOKEN\",\"password\":\"Chosen-Passw0rd\",\"company_name\":\"Fi" + Backslash + "u0001rma\"}",   // a control character
        """["TOKEN"]""",
        "not json",
        "",
    };

    private Task<int> CountAsync<T>(Func<AuthDbContext, IQueryable<T>> set)
        where T : class =>
        InDbAsync(db => set(db).CountAsync(TestContext.Current.CancellationToken));

    private Task<int> UsersWithAsync(string email) =>
        InDbAsync(db => db.Users.CountAsync(u => u.Email == email, TestContext.Current.CancellationToken));

    private Task<int> CompaniesAsync() => CountAsync(db => db.Companies);

    [Fact]
    public async Task A_visitor_signs_up_finishes_and_is_signed_in_as_the_admin_of_a_company_of_their_own()   // criteria 1, 3, 8, 9
    {
        using (var options = await SignupApi.Options(Client))
        {
            Assert.Equal("""{"signup":"self-service"}""", await options.Content.ReadAsStringAsync());
        }

        var token = await MailedAsync("Anna@Firma.test");
        using (var preview = await SignupApi.Preview(Client, token))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        }

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(done);   // 200, the access token and the refresh cookie, as a login answers

        var company = await InDbAsync(db => db.Companies.Where(c => c.Name == CompanyName).Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(
            ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            AccessTokens.Array(session.AccessToken, "permissions"));

        // A login with the chosen password says the same.
        var login = await SessionApi.LoginAsync(Client, "Anna@Firma.test", Password);
        Assert.Equal(company.ToString(), AccessTokens.Text(login.AccessToken, "org_id"));
        Assert.Equal(AccessTokens.Array(session.AccessToken, "permissions"), AccessTokens.Array(login.AccessToken, "permissions"));
    }

    [Fact]
    public async Task The_account_company_roles_and_membership_are_what_the_contract_says()   // criterion 9
    {
        var token = await MailedAsync("Anna@Firma.test");

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken));
        Assert.True(user.EmailConfirmed);
        Assert.Equal("Anna@Firma.test", user.Email);
        Assert.Equal("Anna@Firma.test", user.UserName);
        Assert.NotNull(user.PasswordHash);

        Assert.Equal(2, await CompaniesAsync());   // the development company and this one
        var company = await InDbAsync(db => db.Companies.AsNoTracking().SingleAsync(c => c.Name == CompanyName, TestContext.Current.CancellationToken));
        var roles = await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == company.Id).OrderBy(r => r.Name).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["admin", "user"], roles.Select(r => r.Name));
        Assert.Equal(["*"], roles[0].Permissions);
        Assert.Equal(["reports:read", "reports:approve"], roles[1].Permissions);

        var membership = Assert.Single(await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.CompanyId == company.Id).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(roles[0].Id, membership.RoleId);   // admin
        Assert.Empty(await PendingAsync());
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_company_name_of_the_request_is_only_a_suggestion()   // Review Focus 4
    {
        var token = await MailedAsync(company: CompanyName);

        using var done = await SignupApi.Complete(Client, token, Password, "Żółw & Spółka <Ltd>");
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == CompanyName, TestContext.Current.CancellationToken)));
        var company = await InDbAsync(db => db.Companies.AsNoTracking().SingleAsync(c => c.Name == "Żółw & Spółka <Ltd>", TestContext.Current.CancellationToken));
        var session = await SessionApi.LoginAsync(Client, Address, Password);
        Assert.Equal(company.Id.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
    }

    [Fact]
    public async Task A_weak_password_names_the_rules_creates_nothing_and_leaves_the_token_usable()   // criterion 11
    {
        var token = await MailedAsync();
        var before = Assert.Single(await PendingAsync());

        using (var weak = await SignupApi.Complete(Client, token, "short", CompanyName))
        {
            await AccountApi.AssertErrorAsync(weak, WeakRules);
        }

        Assert.Equal(0, await UsersWithAsync(Address));
        Assert.Equal(1, await CompaniesAsync());   // only the development company
        var after = Assert.Single(await PendingAsync());
        Assert.Equal(before.TokenHash, after.TokenHash);
        using var preview = await SignupApi.Preview(Client, token);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var good = await SignupApi.Complete(Client, token, Password, CompanyName);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Fact]
    public async Task The_order_of_the_checks_is_token_then_password_then_account()   // spec 0009 → To verify
    {
        await CreateUserAsync(Address, confirmed: true);
        var token = await PendingSignupSeed.IssueAsync(Factory.Services, Address, CompanyName);

        using (var unknown = await SignupApi.Complete(Client, EmailTokens.NewToken(), "short", CompanyName))
        {
            await AccountApi.AssertErrorAsync(unknown, AccountApi.InvalidToken);   // the token first
        }

        using (var weak = await SignupApi.Complete(Client, token, "short", CompanyName))
        {
            await AccountApi.AssertErrorAsync(weak, WeakRules);   // then the password, whether or not an account exists
        }

        Assert.Single(await PendingAsync());   // a weak password consumed nothing

        using var good = await SignupApi.Complete(Client, token, Password, CompanyName);
        await SignupApi.AssertAlreadyRegisteredAsync(good);   // then the account
        Assert.Empty(await PendingAsync());
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task Malformed_bodies_are_400_invalid_request_and_leave_the_token_usable(string template)   // criterion 9
    {
        var token = await MailedAsync();

        using var response = await AccountApi.PostRaw(Client, SignupApi.CompletePath, template.Replace("TOKEN", token, StringComparison.Ordinal));

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        Assert.Equal(0, await UsersWithAsync(Address));
        Assert.Equal(1, await CompaniesAsync());
        using var good = await SignupApi.Complete(Client, token, Password, CompanyName);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Fact]
    public async Task A_used_unknown_expired_or_replaced_token_is_400_invalid_token()   // criteria 12, 13
    {
        // Used.
        var used = await MailedAsync("used@firma.test");
        using (var first = await SignupApi.Complete(Client, used, Password, CompanyName))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var second = await SignupApi.Complete(Client, used, Password, CompanyName))
        {
            await AccountApi.AssertErrorAsync(second, AccountApi.InvalidToken);
        }

        using (var preview = await SignupApi.Preview(Client, used))
        {
            await AccountApi.AssertErrorAsync(preview, AccountApi.InvalidToken);
        }

        Assert.Equal(1, await UsersWithAsync("used@firma.test"));

        // Unknown.
        using (var unknown = await SignupApi.Complete(Client, EmailTokens.NewToken(), Password, CompanyName))
        {
            await AccountApi.AssertErrorAsync(unknown, AccountApi.InvalidToken);
        }

        // Replaced by a newer mail.
        var older = await MailedAsync("replaced@firma.test", "First");
        Clock.Advance(PastTheLimit);
        var newer = await MailedAsync("replaced@firma.test", "Second");
        using (var replaced = await SignupApi.Complete(Client, older, Password, CompanyName))
        {
            await AccountApi.AssertErrorAsync(replaced, AccountApi.InvalidToken);
        }

        Assert.Equal(0, await UsersWithAsync("replaced@firma.test"));
        using (var current = await SignupApi.Complete(Client, newer, Password, "Second"))
        {
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        }

        // Expired: 24 hours after the mail.
        var expiring = await MailedAsync("expired@firma.test");
        Clock.Advance(TimeSpan.FromHours(24));
        using var expired = await SignupApi.Complete(Client, expiring, Password, CompanyName);
        await AccountApi.AssertErrorAsync(expired, AccountApi.InvalidToken);
        Assert.Equal(0, await UsersWithAsync("expired@firma.test"));
    }

    [Fact]
    public async Task An_account_made_in_the_meantime_is_409_already_registered_and_the_pending_sign_up_is_gone()   // criterion 14
    {
        var signupToken = await MailedAsync();
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, Address, "user");
        await EnqueueInvitationAsync(invite, Address);
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == Address));
        using (var accepted = await TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd"))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        var companiesBefore = await CompaniesAsync();
        using var done = await SignupApi.Complete(Client, signupToken, Password, "Never Made");

        await SignupApi.AssertAlreadyRegisteredAsync(done);
        Assert.Equal(companiesBefore, await CompaniesAsync());   // creates no company
        Assert.Empty(await PendingAsync());
        using var again = await SignupApi.Complete(Client, signupToken, Password, "Never Made");
        await AccountApi.AssertErrorAsync(again, AccountApi.InvalidToken);
        // The account is the invited one, with the invited password and company.
        var session = await SessionApi.LoginAsync(Client, Address, "Invited-Passw0rd");
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
    }

    [Fact]
    public async Task An_account_under_another_spelling_of_the_address_is_409_too()   // Review Focus 2
    {
        await CreateUserAsync("ANNA@Firma.TEST", confirmed: true);
        var token = await PendingSignupSeed.IssueAsync(Factory.Services, "anna@firma.test", CompanyName);

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);

        await SignupApi.AssertAlreadyRegisteredAsync(done);
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CompaniesAsync());
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task Failed_logins_before_the_sign_up_do_not_lock_the_new_account()   // the streak of an address ends with its account's creation, as an invitation's
    {
        var token = await MailedAsync();
        await LockoutApi.FailAsync(Client, Clock, Address, 5);

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        _ = await SessionApi.LoginAsync(Client, Address, Password);
    }

    [Fact]
    public async Task Nothing_is_left_when_the_audit_row_cannot_be_written()   // criterion 19: the change and its rows, both or neither
    {
        var token = await MailedAsync();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);

        Assert.Equal(HttpStatusCode.InternalServerError, done.StatusCode);
        Assert.Equal(0, await UsersWithAsync(Address));
        Assert.Equal(1, await CompaniesAsync());
        Assert.Equal(0, await InDbAsync(db => db.Memberships.CountAsync(
            m => db.Companies.Any(c => c.Id == m.CompanyId && c.Name == CompanyName), TestContext.Current.CancellationToken)));
        var pending = Assert.Single(await PendingAsync());
        Assert.Equal(EmailTokens.HashOf(token), pending.TokenHash);   // the use of the token was rolled back with the rest
    }

    [Fact]
    public async Task No_token_password_or_address_reaches_the_log()   // criterion 23
    {
        const string Taken = "taken@firma.test";
        const string Boom = "boom@firma.test";
        var token = await MailedAsync();
        var boomToken = await MailedAsync(Boom);
        await CreateUserAsync(Taken, confirmed: true);
        var takenToken = await PendingSignupSeed.IssueAsync(Factory.Services, Taken, CompanyName);

        using (await SignupApi.Complete(Client, token, "short", CompanyName))   // weak password
        {
        }

        SessionApi.Session session;
        using (var signedIn = await SignupApi.Complete(Client, token, Password, CompanyName))   // success: it signs in
        {
            session = await SessionApi.ReadSessionAsync(signedIn);
        }

        using (await SignupApi.Complete(Client, token, Password, CompanyName))   // reuse: invalid_token
        {
        }

        using (await SignupApi.Complete(Client, takenToken, Password, CompanyName))   // 409
        {
        }

        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));
        using (var failed = await SignupApi.Complete(Client, boomToken, Password, CompanyName))   // 500
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }

        foreach (var secret in new[] { token, boomToken, takenToken, Password, Address, Taken, Boom, session.AccessToken, session.RefreshToken })
        {
            Assert.DoesNotContain(secret, Logs.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_completion_is_recorded_with_the_account_the_company_and_the_address_and_nothing_secret()   // criterion 19
    {
        var token = await MailedAsync("Anna@Firma.test");

        using var done = await RateLimitApi.SendAsync(
            Client, HttpMethod.Post, SignupApi.CompletePath, CallerAddress, null,
            $$"""{"token":"{{token}}","password":"{{Password}}","company_name":"{{CompanyName}}"}""");
        var session = await SessionApi.ReadSessionAsync(done);

        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken));
        var company = await InDbAsync(db => db.Companies.AsNoTracking().SingleAsync(c => c.Name == CompanyName, TestContext.Current.CancellationToken));
        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal(user.Id, completed.SubjectUserId);
        Assert.Equal("Anna@Firma.test", completed.SubjectEmail);
        Assert.Equal(company.Id, completed.OrgId);
        Assert.Equal(CompanyName, completed.OrgName);
        Assert.Equal(CallerAddress, completed.ClientIp);
        Assert.Null(completed.ActorUserId);
        Assert.Null(completed.TargetId);
        Assert.Null(completed.Details);

        var created = Assert.Single(await AuditAsync(AuditKinds.OrgCreated), row => row.OrgId == company.Id);
        Assert.Equal(CompanyName, created.OrgName);
        Assert.Equal("signup", AuditApi.Text(created, "via"));
        Assert.Equal(["via"], AuditApi.DetailNames(created));
        await AssertNoSecretsAsync(Password, token, session.AccessToken, session.RefreshToken);
    }
}
