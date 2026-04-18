using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → <c>DELETE /auth/account</c>.</summary>
public sealed class AccountDeletionEndpointTests : AuditTestBase
{
    private const string Private = "ola@poczta.test";
    private const string Path = "/auth/account";

    // Spelled out, so that the escape in a test body is not read as an escape of this file.
    private const char Backslash = (char)0x5C;

    public AccountDeletionEndpointTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup();   // a new sign-up after the deletion
    }

    private Task<HttpResponseMessage> DeleteAsync(string? token, string password = UserPassword) =>
        TenancyApi.Send(Client, HttpMethod.Delete, Path, token, new { password });

    private async Task<HttpResponseMessage> RawAsync(string token, string body, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return await Client.SendAsync(request);
    }

    private async Task<(Guid User, Guid Space, SessionApi.Session Session)> PrivateAsync()
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        return (user, space, await SessionApi.LoginAsync(Client, Private, UserPassword));
    }

    private Task<int> UsersAsync(Guid user) => InDbAsync(db => db.Users.CountAsync(u => u.Id == user, TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_private_account_deletes_itself_with_its_password_204_and_the_cookie_is_cleared()   // criterion 21
    {
        var (user, space, session) = await PrivateAsync();
        await PendingSignupSeed.IssueAsync(Factory.Services, Private, null, SignupKind.Private);
        await EnqueueAsync(MailKind.PasswordReset, Private);   // a queued request of the address
        Assert.Equal(1, await InDbAsync(db => db.PendingSignups.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
        var subject = user.ToString();
        var logins = (await AuditAsync(AuditKinds.LoginSucceeded)).Count;

        using var response = await SendFromCallerAsync(HttpMethod.Delete, Path, session.AccessToken, new { password = UserPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        var cookie = SessionApi.RefreshCookieOf(response);
        Assert.Equal(TimeSpan.Zero, cookie.MaxAge);
        Assert.Equal("", cookie.Value.Value);

        var cancellation = TestContext.Current.CancellationToken;
        Assert.Equal(0, await UsersAsync(user));
        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == space, cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user, cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync(t => t.Subject == subject, cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>().CountAsync(a => a.Subject == subject, cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.PendingSignups.CountAsync(cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(cancellation)));
        Assert.Equal(logins, (await AuditAsync(AuditKinds.LoginSucceeded)).Count);   // the rows that name it remain

        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(user, row.ActorUserId);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal(Private, row.SubjectEmail);
        Assert.Equal(space, row.OrgId);
        Assert.Equal(PersonalSpaces.Name, row.OrgName);
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal("self", AuditApi.Text(row, "reason"));

        using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refresh);
        using var me = await TenancyApi.Get(Client, OrgEndpoints.MePath, session.AccessToken);   // the access token lives on for its minutes
        await TenancyApi.AssertErrorAsync(me, HttpStatusCode.Forbidden, "permissions_changed");
        await AssertNoSecretsAsync(UserPassword, session.AccessToken, session.RefreshToken);
    }

    [Fact]
    public async Task A_login_afterwards_is_the_401_of_an_unknown_address_and_a_new_sign_up_completes()   // criterion 21
    {
        var (user, _, session) = await PrivateAsync();
        using (var deleted = await DeleteAsync(session.AccessToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var login = await LoginApi.Login(Client, Private, UserPassword))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
            Assert.Equal("""{"error":"invalid_credentials"}""", await login.Content.ReadAsStringAsync());
        }

        var token = await PendingSignupSeed.IssueAsync(Factory.Services, Private, null, SignupKind.Private);
        using var again = await SignupApi.CompletePrivate(Client, token, "Chosen-Passw0rd");
        var fresh = await SessionApi.ReadSessionAsync(again);
        Assert.NotEqual(user.ToString(), AccessTokens.Text(fresh.AccessToken, "sub"));
        Assert.Equal("personal", AccessTokens.Text(fresh.AccessToken, "org_kind"));
    }

    [Fact]
    public async Task A_password_stored_in_an_older_format_is_accepted_and_the_deletion_stands()
    {
        var (user, _, session) = await PrivateAsync();
        var legacy = new PasswordHasher<ApplicationUser>(
            Options.Create(new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }))
            .HashPassword(new ApplicationUser(), UserPassword);
        await InDbAsync(db => db.Users.Where(u => u.Id == user).ExecuteUpdateAsync(
            s => s.SetProperty(u => u.PasswordHash, legacy), TestContext.Current.CancellationToken));
        // The precondition: the default hasher would store a new hash after a check with this one.
        Assert.Equal(
            PasswordVerificationResult.SuccessRehashNeeded,
            new PasswordHasher<ApplicationUser>().VerifyHashedPassword(new ApplicationUser(), legacy, UserPassword));

        using var response = await DeleteAsync(session.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await UsersAsync(user));
        Assert.Single(await AuditAsync(AuditKinds.AccountDeleted));
    }

    [Fact]
    public async Task A_password_changed_while_the_request_waits_for_the_address_lock_is_judged_by_the_new_one()   // spec 0010 → Locks
    {
        var (user, _, session) = await PrivateAsync();
        var changed = new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "Changed-Passw0rd");
        Task<HttpResponseMessage> pending;

        await using (var held = await HeldLock.AddressAsync(Factory, "OLA@POCZTA.TEST"))
        {
            pending = DeleteAsync(session.AccessToken);   // the password it was sent with is the old one
            await HeldLock.WaitForWaitersAsync(Factory, 1);
            await InDbAsync(db => db.Users.Where(u => u.Id == user).ExecuteUpdateAsync(
                s => s.SetProperty(u => u.PasswordHash, changed), TestContext.Current.CancellationToken));
            await held.ReleaseAsync();
        }

        using var response = await pending;
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(1, await UsersAsync(user));
    }

    [Fact]
    public async Task Without_a_token_it_is_401()   // criterion 22
    {
        using var response = await DeleteAsync(null);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_that_says_personal_after_the_account_moved_to_a_company_is_permissions_changed()   // criterion 22
    {
        var (user, _, session) = await PrivateAsync();
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, Private, "user");
        await EnqueueInvitationAsync(invite, Private);
        await DispatchAsync();
        using (var moved = await TenancyApi.AcceptLeaving(Client, TokenIn(Mail.Sent.Last(m => m.To == Private))))
        {
            Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        }

        using var response = await DeleteAsync(session.AccessToken);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");   // never forbidden: the frontend refreshes
        Assert.Equal(1, await UsersAsync(user));
    }

    [Fact]
    public async Task An_account_with_a_personal_space_and_a_company_is_a_company_member_at_sign_in_and_cannot_delete_itself()   // criterion 22
    {
        var (user, _, _) = await PrivateAsync();
        await AddLaterMembershipAsync(user, await DevCompanyIdAsync(), "user");   // the personal membership is the older one
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        Assert.Equal("organization", AccessTokens.Text(session.AccessToken, "org_kind"));

        using var response = await DeleteAsync(session.AccessToken);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(1, await UsersAsync(user));
        Assert.Equal(2, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_token_that_says_personal_after_the_account_is_gone_is_permissions_changed()   // criterion 22
    {
        var (_, _, session) = await PrivateAsync();
        using (var first = await DeleteAsync(session.AccessToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        using var again = await DeleteAsync(session.AccessToken);   // the access token lives on for its minutes

        await TenancyApi.AssertErrorAsync(again, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Fact]
    public async Task A_company_member_is_403_forbidden_and_nothing_is_counted()   // criterion 22
    {
        var token = (await SessionApi.LoginAsync(Client, Factory)).AccessToken;

        using var response = await DeleteAsync(token, "Wrong-Password-1");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
        Assert.Equal(1, await UsersAsync(await Factory.SeedUserIdAsync()));
    }

    public static TheoryData<string> BadBodies => new()
    {
        "{}",
        """{"password":""}""",
        """{"password":"   "}""",
        """{"password":5}""",
        """{"password":null}""",
        """["password"]""",
        "not json",
        "{\"password\":\"Another" + Backslash + "u0000Passw0rd\"}",   // NUL in the password
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request_and_counts_for_nothing(string body)   // criterion 22
    {
        var (user, _, session) = await PrivateAsync();

        using var response = await RawAsync(session.AccessToken, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await UsersAsync(user));
    }

    [Fact]
    public async Task A_wrong_password_is_403_wrong_password_counted_and_recorded()   // criterion 22
    {
        var (user, space, session) = await PrivateAsync();

        using var response = await DeleteAsync(session.AccessToken, "Wrong-Password-1");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(1, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));   // counted as a failed login
        Assert.Equal(1, await UsersAsync(user));
        var row = await SingleAsync(AuditKinds.AccountDeleteRefused);
        Assert.Equal(user, row.ActorUserId);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal(Private, row.SubjectEmail);
        Assert.Equal(space, row.OrgId);
        Assert.Equal(PersonalSpaces.Name, row.OrgName);
        Assert.Equal("wrong_password", AuditApi.Text(row, "reason"));
        await AssertNoSecretsAsync("Wrong-Password-1", UserPassword);
    }

    [Fact]
    public async Task A_locked_identifier_is_429_too_many_attempts_and_recorded()   // criterion 22
    {
        var (user, _, session) = await PrivateAsync();
        await LockoutApi.FailAsync(Client, Clock, Private, 10);

        using var response = await DeleteAsync(session.AccessToken);   // even the right password

        await LockoutApi.AssertLockedAsync(response);
        Assert.Equal(1, await UsersAsync(user));
        Assert.Equal("locked", AuditApi.Text(await SingleAsync(AuditKinds.AccountDeleteRefused), "reason"));
    }

    [Fact]
    public async Task A_body_in_another_charset_is_415_and_one_that_is_not_json_is_400()   // criterion 22
    {
        var (_, _, session) = await PrivateAsync();

        using var utf16 = await RawAsync(session.AccessToken, """{"password":"x"}""", "application/json; charset=utf-16");
        using var plain = await RawAsync(session.AccessToken, """{"password":"x"}""", "text/plain");

        await TenancyApi.AssertErrorAsync(utf16, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        await TenancyApi.AssertErrorAsync(plain, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_deletion_that_finds_the_space_no_longer_personal_under_the_lock_is_permissions_changed()   // spec 0010 → Locks
    {
        var (user, space, session) = await PrivateAsync();
        var userRole = await RoleIdAsync(await DevCompanyIdAsync(), "user");
        Task<HttpResponseMessage> pending;

        await using (var held = await HeldLock.AddressAsync(Factory, "OLA@POCZTA.TEST"))
        {
            pending = DeleteAsync(session.AccessToken);
            await HeldLock.WaitForWaitersAsync(Factory, 1);   // checked, waiting for the address

            // Meanwhile the account moves into a company (as an invitation would, which takes the address lock too).
            await InDbAsync(async db =>
            {
                await db.Memberships.Where(m => m.UserId == user).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
                await db.Companies.Where(c => c.Id == space).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
                db.Memberships.Add(new Membership { UserId = user, CompanyId = await DevCompanyIdAsync(), RoleId = userRole, JoinedAt = StorableTime.Now(Clock) });
                return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            });
            await held.ReleaseAsync();
        }

        using var response = await pending;
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
        Assert.Equal(1, await UsersAsync(user));
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleted));
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));   // nothing was counted
    }
}
