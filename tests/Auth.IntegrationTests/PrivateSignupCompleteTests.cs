using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → preview and complete of a private sign-up, and of an organization sign-up as the kind changes it.</summary>
public sealed class PrivateSignupCompleteTests : SignupTestBase
{
    private const string Private = "ola@poczta.test";
    private const string WeakRules = """{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}""";

    public PrivateSignupCompleteTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup();
    }

    private Task<int> UsersWithAsync(string normalized) =>
        InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == normalized, TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_private_person_signs_up_through_the_mail_and_is_signed_in_with_a_personal_space()   // criteria 8, 9
    {
        var token = await MailedPrivateAsync(Private);

        using (var preview = await SignupApi.Preview(Client, token))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal("""{"kind":"private","email":"ola@poczta.test"}""", await preview.Content.ReadAsStringAsync());
        }

        using var done = await SignupApi.CompletePrivate(Client, token, Password);
        var session = await SessionApi.ReadSessionAsync(done);   // the answer of a login: the access token and the refresh cookie

        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "OLA@POCZTA.TEST", TestContext.Current.CancellationToken));
        Assert.True(user.EmailConfirmed);
        var space = await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.UserId == user.Id)
            .Join(db.Companies.AsNoTracking(), m => m.CompanyId, c => c.Id, (m, c) => c).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CompanyKind.Personal, space.Kind);
        Assert.Equal(PersonalSpaces.Name, space.Name);
        Assert.Single(await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == space.Id).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == space.Id, TestContext.Current.CancellationToken)));

        Assert.Equal(space.Id.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal("personal", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(["private"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(["reports:approve", "reports:read"], AccessTokens.Array(session.AccessToken, "permissions"));

        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal(user.Id, completed.SubjectUserId);
        Assert.Equal(space.Id, completed.OrgId);
        Assert.Equal(PersonalSpaces.Name, completed.OrgName);
        Assert.Equal("private", AuditApi.Text(completed, "kind"));
        Assert.Equal(["kind"], AuditApi.DetailNames(completed));
        Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded), row => row.SubjectUserId == user.Id && row.OrgId == space.Id);
        Assert.DoesNotContain(await AuditAsync(), row => row.Kind.StartsWith("org.", StringComparison.Ordinal) && row.OrgId == space.Id);
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task A_company_name_a_missing_password_or_a_weak_one_is_400_and_the_link_still_works()   // criterion 10
    {
        var token = await MailedPrivateAsync(Private);

        using (var named = await AccountApi.PostRaw(Client, SignupApi.CompletePath, $$"""{"token":"{{token}}","password":"{{Password}}","company_name":"Firma"}"""))
        {
            await AccountApi.AssertErrorAsync(named, AccountApi.InvalidRequest);
        }

        using (var missing = await AccountApi.PostRaw(Client, SignupApi.CompletePath, $$"""{"token":"{{token}}"}"""))
        {
            await AccountApi.AssertErrorAsync(missing, AccountApi.InvalidRequest);
        }

        using (var weak = await SignupApi.CompletePrivate(Client, token, "abc"))
        {
            await AccountApi.AssertErrorAsync(weak, WeakRules);
        }

        Assert.Equal(0, await UsersWithAsync("OLA@POCZTA.TEST"));
        using var good = await SignupApi.CompletePrivate(Client, token, Password);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("company")]
    public async Task An_address_that_has_any_account_by_then_is_409_and_the_pending_sign_up_is_gone(string account)   // criterion 10
    {
        var token = await MailedPrivateAsync(Private);
        if (account == "private")
        {
            await CreatePrivateAccountAsync(Private);
        }
        else
        {
            await AddMemberAsync(await DevCompanyIdAsync(), Private, "user");
        }

        using var response = await SignupApi.CompletePrivate(Client, token, Password);

        await SignupApi.AssertAlreadyRegisteredAsync(response);
        Assert.Empty(await PendingAsync());
        Assert.Equal(1, await UsersWithAsync("OLA@POCZTA.TEST"));
    }

    [Fact]
    public async Task Of_parallel_private_completions_with_one_token_exactly_one_succeeds()   // criterion 10
    {
        var token = await MailedPrivateAsync(Private);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => SignupApi.CompletePrivate(Client, token, Password))));
        try
        {
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                await AccountApi.AssertErrorAsync(refused, AccountApi.InvalidToken);
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Assert.Equal(1, await UsersWithAsync("OLA@POCZTA.TEST"));
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Kind == CompanyKind.Personal, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task An_organization_completion_says_organization_in_the_token_and_its_kind_in_the_audit_row()   // criterion 11
    {
        var token = await MailedAsync();

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(done);

        Assert.Equal("organization", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal("organization", AuditApi.Text(completed, "kind"));
        Assert.Equal(["kind"], AuditApi.DetailNames(completed));
    }

    [Fact]
    public async Task An_organization_completion_without_a_company_name_is_400_and_the_token_stays_usable()   // spec 0010 → Complete, step 5
    {
        var token = await MailedAsync();

        using (var unnamed = await SignupApi.CompletePrivate(Client, token, Password))
        {
            await AccountApi.AssertErrorAsync(unnamed, AccountApi.InvalidRequest);
        }

        using var named = await SignupApi.Complete(Client, token, Password, CompanyName);
        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
    }

    [Fact]
    public async Task A_missing_company_name_or_password_with_an_unknown_token_is_invalid_token()   // spec 0010, Settled 5
    {
        var unknown = new string('x', 43);

        using var noName = await SignupApi.CompletePrivate(Client, unknown, Password);
        using var noPassword = await AccountApi.PostRaw(Client, SignupApi.CompletePath, $$"""{"token":"{{unknown}}"}""");

        await AccountApi.AssertErrorAsync(noName, AccountApi.InvalidToken);
        await AccountApi.AssertErrorAsync(noPassword, AccountApi.InvalidToken);
    }

    [Fact]
    public async Task A_private_link_after_private_sign_up_was_turned_off_is_403_and_stays_usable()   // criterion 3
    {
        var token = await MailedPrivateAsync(Private);
        var on = Holder.Current;
        Holder.Set(new Manifest(on.Permissions, on.DefaultRoles, on.Signup, privateSignup: false, on.PrivatePermissions), null);   // organization stays on

        using (var preview = await SignupApi.Preview(Client, token))
        {
            await SignupApi.AssertDisabledAsync(preview);
        }

        using (var refused = await SignupApi.CompletePrivate(Client, token, Password))
        {
            await SignupApi.AssertDisabledAsync(refused);
        }

        Assert.Single(await PendingAsync());
        Holder.Set(on, null);
        using var done = await SignupApi.CompletePrivate(Client, token, Password);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
    }
}
