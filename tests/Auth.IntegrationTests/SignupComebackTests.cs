using System.Net;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0009, Amendment 3, preview and completion: a person whose account has no company (the company was deleted, or they were removed from it)
/// finishes a sign-up with only the token and a company name; the account keeps its password, its email is confirmed, and the person is signed in.
/// </summary>
public sealed class SignupComebackTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    private const string NewCompany = "Firma Again";
    private const string Backslash = "\\";

    // The triggers of SignupCompleteSignInTests: the database refuses the audit row of the sign-in, after the completion has done its own work.
    private const string RefuseLoginSucceeded =
        "CREATE FUNCTION refuse_row() RETURNS trigger AS $$ BEGIN IF NEW.kind = 'login.succeeded' THEN RAISE EXCEPTION 'refused'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql";
    private const string RefuseAudit =
        "CREATE TRIGGER refuse_row BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION refuse_row()";
    private const string AllowAudit = "DROP TRIGGER refuse_row ON audit_events";

    private static readonly string Mu = char.ConvertFromUtf32(0x3BC);
    private static readonly string Micro = char.ConvertFromUtf32(0xB5);
    private static readonly string FinalSigma = char.ConvertFromUtf32(0x3C2);
    private static readonly string Sigma = char.ConvertFromUtf32(0x3C3);

    public static TheoryData<string, string> FoldedPairs => new()
    {
        { Mu + "aria@firma.test", Micro + "aria@firma.test" },
        { "kosta" + FinalSigma + "@firma.test", "kosta" + Sigma + "@firma.test" },
    };

    public static TheoryData<string> MalformedPasswords => new()
    {
        """{"token":"TOKEN","company_name":"Firma Again","password":123}""",
        """{"token":"TOKEN","company_name":"Firma Again","password":""}""",
        """{"token":"TOKEN","company_name":"Firma Again","password":"   "}""",
        """{"token":"TOKEN","company_name":"Firma Again","password":null}""",
        "{\"token\":\"TOKEN\",\"company_name\":\"Firma Again\",\"password\":\"Chosen" + Backslash + "u0000Passw0rd\"}",   // NUL in the password
    };

    private Task<ApplicationUser> UserAsync(Guid id) =>
        InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id, TestContext.Current.CancellationToken));

    private Task<Guid> CompanyIdAsync(string name) =>
        InDbAsync(db => db.Companies.Where(c => c.Name == name).Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken));

    private Task<int> CompaniesNamedAsync(string name) =>
        InDbAsync(db => db.Companies.CountAsync(c => c.Name == name, TestContext.Current.CancellationToken));

    [Fact]
    public async Task Preview_says_has_account_for_an_account_without_a_company_and_only_for_it()   // criteria 8 and 24
    {
        // A new address.
        var fresh = await MailedAsync("new@firma.test");
        using (var preview = await SignupApi.Preview(Client, fresh))
        {
            var body = await TenancyApi.ReadOkAsync(preview);
            Assert.Equal(["company_name", "email", "has_account"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.False(body.GetProperty("has_account").GetBoolean());
        }

        // An account without a company, asked for in another case.
        await RemovedMemberAsync("Back@Firma.Test");
        var returning = await MailedAsync("back@firma.test", "Asked for");
        var before = Assert.Single(await PendingAsync(), p => p.NormalizedEmail == "BACK@FIRMA.TEST");
        for (var i = 0; i < 2; i++)
        {
            using var preview = await SignupApi.Preview(Client, returning);
            var body = await TenancyApi.ReadOkAsync(preview);
            Assert.True(body.GetProperty("has_account").GetBoolean());
            Assert.Equal("back@firma.test", body.GetProperty("email").GetString());   // the typed address
            Assert.Equal("Asked for", body.GetProperty("company_name").GetString());
        }

        var after = Assert.Single(await PendingAsync(), p => p.NormalizedEmail == "BACK@FIRMA.TEST");
        Assert.Equal(before.TokenHash, after.TokenHash);   // the preview changes nothing

        // An account that has joined a company since the mail: no longer "has_account".
        var userId = await RemovedMemberAsync("joined@firma.test");
        var joined = await MailedAsync("joined@firma.test");
        await JoinACompanyAsync(userId);
        using var last = await SignupApi.Preview(Client, joined);
        Assert.False((await TenancyApi.ReadOkAsync(last)).GetProperty("has_account").GetBoolean());
    }

    [Fact]
    public async Task A_person_without_a_company_comes_back_with_only_the_token_and_a_company_name()   // criterion 24
    {
        var userId = await RemovedMemberAsync();
        var before = await UserAsync(userId);
        var token = await MailedAsync();

        using var done = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        var session = await SessionApi.ReadSessionAsync(done);   // 200, the access token and the refresh cookie, as a login answers

        var company = await CompanyIdAsync(NewCompany);
        Assert.Equal(userId.ToString(), new JsonWebToken(session.AccessToken).Subject);
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        var membership = Assert.Single(await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.CompanyId == company).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(userId, membership.UserId);
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken)));
        Assert.Empty(await PendingAsync());

        // The same account: its id, its address as it spells it and its password are as they were.
        var after = await UserAsync(userId);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.UserName, after.UserName);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.True(after.EmailConfirmed);

        // ...so the password it always had signs it in, to the new company.
        var login = await SessionApi.LoginAsync(Client, Address, UserPassword);
        Assert.Equal(company.ToString(), AccessTokens.Text(login.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(login.AccessToken, "roles"));

        // The link is used up.
        using var again = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        await AccountApi.AssertErrorAsync(again, AccountApi.InvalidToken);
    }

    [Theory]
    [InlineData("Brand-New-Passw0rd")]
    [InlineData("x")]
    public async Task A_password_in_the_body_is_not_used(string sent)   // criterion 24: it may be absent, and when present it is ignored
    {
        await RemovedMemberAsync();
        var token = await MailedAsync();

        using var done = await SignupApi.Complete(Client, token, sent, NewCompany);   // even a weak one: it is not checked

        _ = await SessionApi.ReadSessionAsync(done);
        _ = await SessionApi.LoginAsync(Client, Address, UserPassword);   // the password the account had still works
        using var other = await LoginApi.Login(Client, Address, sent);
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);      // and the one that was sent was not set
    }

    [Fact]
    public async Task An_unconfirmed_address_is_confirmed_by_the_link()   // criterion 24
    {
        var userId = await RemovedMemberAsync(confirmed: false);
        Assert.False((await UserAsync(userId)).EmailConfirmed);
        var token = await MailedAsync();

        using var done = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        _ = await SessionApi.ReadSessionAsync(done);

        Assert.True((await UserAsync(userId)).EmailConfirmed);
        _ = await SessionApi.LoginAsync(Client, Address, UserPassword);   // an unconfirmed address could not log in
    }

    [Fact]
    public async Task The_completion_is_recorded_with_existing_account_true_beside_login_succeeded_and_org_created()   // criterion 19 and 24
    {
        var userId = await RemovedMemberAsync("Anna@Firma.test");
        var token = await MailedAsync("anna@firma.test");   // asked for in another case

        using var done = await RateLimitApi.SendAsync(
            Client, HttpMethod.Post, SignupApi.CompletePath, CallerAddress, null,
            $$"""{"token":"{{token}}","company_name":"{{NewCompany}}"}""");
        var session = await SessionApi.ReadSessionAsync(done);

        var company = await CompanyIdAsync(NewCompany);
        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal(userId, completed.SubjectUserId);
        Assert.Equal("Anna@Firma.test", completed.SubjectEmail);   // as the account stores it
        Assert.Equal(company, completed.OrgId);
        Assert.Equal(NewCompany, completed.OrgName);
        Assert.Equal(CallerAddress, completed.ClientIp);
        Assert.Null(completed.ActorUserId);
        Assert.Equal(["existing_account"], AuditApi.DetailNames(completed));
        Assert.Equal("true", AuditApi.Raw(completed, "existing_account"));

        var created = Assert.Single(await AuditAsync(AuditKinds.OrgCreated), row => row.OrgId == company);
        Assert.Equal("signup", AuditApi.Text(created, "via"));
        var signedIn = await SingleAsync(AuditKinds.LoginSucceeded);
        Assert.Equal(userId, signedIn.SubjectUserId);
        Assert.Equal(company, signedIn.OrgId);
        await AssertNoSecretsAsync(token, session.AccessToken, session.RefreshToken);
    }

    [Fact]
    public async Task Sessions_from_before_the_company_was_lost_end_with_the_completion()   // defence in depth, as at an invitation: the product's removal revokes sessions itself; this pins that the completion does too
    {
        var company = await CreateCompanyAsync("Former company");
        var userId = await AddMemberAsync(company, Address, "user");
        var old = await SessionApi.LoginAsync(Client, Address, UserPassword);   // a session of the former membership
        await RemoveMembershipAsync(userId);                                     // straight in the database: no session is ended (the product's removal would have ended it)
        var token = await MailedAsync();

        using var done = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        var fresh = await SessionApi.ReadSessionAsync(done);

        using (var stale = await SessionApi.Refresh(Client, old.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(stale);
        }

        var refreshed = await SessionApi.RefreshOk(Client, fresh.RefreshToken);   // the session of the completion is the one that works
        Assert.Equal(AccessTokens.Text(fresh.AccessToken, "org_id"), AccessTokens.Text(refreshed.AccessToken, "org_id"));
    }

    [Theory]
    [MemberData(nameof(MalformedPasswords))]
    public async Task A_malformed_password_is_400_invalid_request_for_such_an_account_too_and_the_token_stays_usable(string template)   // criterion 24: a member that is present is held to the rule
    {
        await RemovedMemberAsync();
        var token = await MailedAsync();

        using (var response = await AccountApi.PostRaw(Client, SignupApi.CompletePath, template.Replace("TOKEN", token, StringComparison.Ordinal)))
        {
            await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        }

        Assert.Equal(0, await CompaniesNamedAsync(NewCompany));
        using var good = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Fact]
    public async Task A_new_address_without_a_password_is_400_invalid_request_and_the_token_stays_usable()   // the password is still required of everyone but an account without a company
    {
        var token = await MailedAsync();

        using (var without = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany))
        {
            await AccountApi.AssertErrorAsync(without, AccountApi.InvalidRequest);
        }

        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Address, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CompaniesNamedAsync(NewCompany));
        Assert.Equal(EmailTokens.HashOf(token), Assert.Single(await PendingAsync()).TokenHash);   // rolled back with the rest
        using var good = await SignupApi.Complete(Client, token, Password, NewCompany);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Fact]
    public async Task An_unknown_token_without_a_password_is_invalid_token()   // the order of the checks: the token first
    {
        using var unknown = await SignupApi.CompleteWithoutPassword(Client, EmailTokens.NewToken(), NewCompany);

        await AccountApi.AssertErrorAsync(unknown, AccountApi.InvalidToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_account_that_joined_a_company_meanwhile_is_409_and_the_pending_sign_up_is_gone(bool withPassword)   // criterion 24
    {
        var userId = await RemovedMemberAsync();
        var token = await MailedAsync();
        await JoinACompanyAsync(userId);   // an invitation accepted since the mail, say
        var companiesBefore = await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken));

        // The screen was told has_account and sent no password: the answer is the 409 all the same.
        using var done = withPassword
            ? await SignupApi.Complete(Client, token, Password, NewCompany)
            : await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);

        await SignupApi.AssertAlreadyRegisteredAsync(done);
        Assert.Equal(companiesBefore, await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));   // creates no company
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == userId, TestContext.Current.CancellationToken)));
        Assert.Empty(await PendingAsync());
        using var again = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        await AccountApi.AssertErrorAsync(again, AccountApi.InvalidToken);
    }

    [Theory]
    [MemberData(nameof(FoldedPairs))]
    public async Task An_account_under_a_spelling_the_normaliser_folds_is_409_and_is_left_alone(string account, string typed)   // the link proves the typed mailbox only
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var normalizer = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>();
            Assert.Equal(normalizer.NormalizeEmail(account), normalizer.NormalizeEmail(typed));   // the premise
        }

        var userId = await RemovedMemberAsync(account, confirmed: false);
        var before = await UserAsync(userId);
        var token = await PendingSignupSeed.IssueAsync(Factory.Services, typed, CompanyName);

        using (var preview = await SignupApi.Preview(Client, token))
        {
            Assert.False((await TenancyApi.ReadOkAsync(preview)).GetProperty("has_account").GetBoolean());
        }

        using var done = await SignupApi.Complete(Client, token, Password, NewCompany);

        await SignupApi.AssertAlreadyRegisteredAsync(done);
        var after = await UserAsync(userId);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.False(after.EmailConfirmed);   // the link did not confirm an address it was not sent to
        Assert.Equal(0, await CompaniesNamedAsync(NewCompany));
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task A_sign_in_that_cannot_be_written_leaves_the_account_as_it_was()   // Amendment 2 for a comeback: the sign-in is part of the completion
    {
        var userId = await RemovedMemberAsync(confirmed: false);
        var token = await MailedAsync();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseLoginSucceeded, TestContext.Current.CancellationToken));
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseAudit, TestContext.Current.CancellationToken));

        using (var failed = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.False(failed.Headers.Contains("Set-Cookie"), "A completion that failed must not write the cookie.");
        }

        var unchanged = await UserAsync(userId);
        Assert.False(unchanged.EmailConfirmed);   // the confirmation went with the rollback
        Assert.Equal(0, await CompaniesNamedAsync(NewCompany));
        Assert.Equal(0, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == userId, TestContext.Current.CancellationToken)));
        Assert.Empty(await AuditAsync(AuditKinds.SignupCompleted));
        Assert.Equal(EmailTokens.HashOf(token), Assert.Single(await PendingAsync()).TokenHash);   // the link is as it was

        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(AllowAudit, TestContext.Current.CancellationToken));
        using var again = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        _ = await SessionApi.ReadSessionAsync(again);
        Assert.True((await UserAsync(userId)).EmailConfirmed);
    }
}
