using System.Net;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class AuditAccountFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Remote = "203.0.113.5";
    private const string NewPassword = "Brand-New-Passw0rd";
    private static readonly TimeSpan PastTheGap = TimeSpan.FromSeconds(61);

    private Task<HttpResponseMessage> LoginFromAsync(string email, string password, string remote = Remote) =>
        RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", remote, null, System.Text.Json.JsonSerializer.Serialize(new { email, password }));

    // ---- login

    [Fact]
    public async Task A_login_that_succeeds_is_recorded_with_the_account_the_company_and_the_address()   // criterion 8
    {
        using var response = await LoginFromAsync(Factory.SeedEmail, Factory.SeedPassword);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await SingleAsync(AuditKinds.LoginSucceeded);
        var seed = await Factory.SeedUserIdAsync();
        Assert.Equal(seed, row.ActorUserId);
        Assert.Equal(seed, row.SubjectUserId);
        Assert.Equal(Factory.SeedEmail, row.SubjectEmail);
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal(DevUserSeeder.DefaultOrgName, row.OrgName);
        Assert.Equal(Remote, row.ClientIp);
        Assert.Null(row.TargetId);
        Assert.Empty(await AuditAsync(AuditKinds.LoginFailed));
        await AssertNoSecretsAsync(Factory.SeedPassword);
    }

    [Fact]
    public async Task A_wrong_password_is_recorded_with_the_account_as_stored_and_the_reason()   // criterion 8
    {
        using var response = await LoginFromAsync(Factory.SeedEmail.ToUpperInvariant(), WrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Null(row.ActorUserId);   // a failed login has no actor
        Assert.Equal(await Factory.SeedUserIdAsync(), row.SubjectUserId);
        Assert.Equal(Factory.SeedEmail, row.SubjectEmail);   // as stored, not as typed
        Assert.Equal("wrong_password", AuditApi.Text(row, "reason"));
        Assert.Equal(Remote, row.ClientIp);
        await AssertNoSecretsAsync(WrongPassword, Factory.SeedPassword);
    }

    [Fact]
    public async Task An_address_nobody_has_is_recorded_as_typed_with_no_account()   // criterion 8
    {
        using var response = await LoginFromAsync("Nobody@Example.test", WrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.SubjectUserId);
        Assert.Equal("Nobody@Example.test", row.SubjectEmail);
        Assert.Equal("unknown_address", AuditApi.Text(row, "reason"));
        await AssertNoSecretsAsync(WrongPassword);
    }

    [Fact]
    public async Task An_account_with_no_password_yet_is_recorded_like_an_address_nobody_has()   // an invited account that has not accepted
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await users.CreateAsync(new ApplicationUser { UserName = "invited@example.com", Email = "invited@example.com", EmailConfirmed = true });
            Assert.True(created.Succeeded);
        }

        using var response = await LoginFromAsync("Invited@Example.com", WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);   // the answer of an unknown address (no enumeration)
        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Null(row.SubjectUserId);                                    // and so is its row: no account is named
        Assert.Equal("Invited@Example.com", row.SubjectEmail);
        Assert.Equal("unknown_address", AuditApi.Text(row, "reason"));
    }

    [Fact]
    public async Task A_login_whose_row_cannot_be_written_issues_no_session()   // criterion 8: a session and its row are both made, or neither
    {
        var tokensBefore = await TokenCountAsync();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var response = await LoginFromAsync(Factory.SeedEmail, Factory.SeedPassword);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("""{"error":"internal_error"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(response.Headers.Contains("Set-Cookie"), "no refresh cookie may leave for a session that was rolled back");
        Assert.Equal(tokensBefore, await TokenCountAsync());   // OpenIddict's authorization and token entries were rolled back with it
        Assert.Equal(0, await InDbAsync(db => db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "OpenIddictAuthorizations" """)
            .SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_logout_whose_row_cannot_be_written_ends_nothing()   // criterion 8: the revocation and its row are both made, or neither
    {
        var session = await SessionApi.LoginAsync(Client, Factory);
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var logout = await SessionApi.Logout(Client, session.RefreshToken);

        Assert.Equal(HttpStatusCode.InternalServerError, logout.StatusCode);
        using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);   // the session was not revoked: the cookie still refreshes
    }

    private Task<int> TokenCountAsync() =>
        InDbAsync(db => db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "OpenIddictTokens" """).SingleAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_correct_password_for_an_unconfirmed_address_is_recorded_as_a_failed_login()   // criterion 8
    {
        var user = await CreateUserAsync("new@example.com", confirmed: false);

        using var response = await LoginFromAsync("new@example.com", UserPassword);
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "email_not_verified");

        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Equal(user.Id, row.SubjectUserId);
        Assert.Equal("unconfirmed_address", AuditApi.Text(row, "reason"));
        await AssertNoSecretsAsync(UserPassword);
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task A_correct_password_for_an_account_without_a_company_is_recorded_as_a_failed_login()   // criterion 8
    {
        var user = await CreateUserAsync("lonely@example.com", confirmed: true, member: false);

        using var response = await LoginFromAsync("lonely@example.com", UserPassword);
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");

        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Equal(user.Id, row.SubjectUserId);
        Assert.Equal("no_company", AuditApi.Text(row, "reason"));
    }

    [Fact]
    public async Task A_locked_identifier_is_recorded_with_the_address_as_typed_and_the_wait()   // criterion 8
    {
        await LockoutApi.FailAsync(Client, Clock, "ghost@example.test", 10);

        using var refused = await LoginFromAsync("Ghost@Example.test", WrongPassword);
        var seconds = await LockoutApi.AssertLockedAsync(refused);

        var row = await SingleAsync(AuditKinds.LoginLocked);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.SubjectUserId);   // a locked attempt looks nothing up (spec 0003)
        Assert.Equal("Ghost@Example.test", row.SubjectEmail);
        Assert.Equal(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), AuditApi.Text(row, "retry_after_seconds"));
        Assert.Equal(Remote, row.ClientIp);
        Assert.Equal(10, (await AuditAsync(AuditKinds.LoginFailed)).Count);   // the ten failures that led here
    }

    [Fact]
    public async Task A_login_the_server_cannot_read_writes_nothing()
    {
        using var malformed = await RateLimitApi.SendAsync(Client, HttpMethod.Post, "/auth/login", Remote, null, "{ not json");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        Assert.Empty(await AuditAsync(AuditKinds.LoginFailed));
        Assert.Empty(await AuditAsync(AuditKinds.LoginLocked));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    // ---- logout

    [Fact]
    public async Task A_logout_names_the_account_of_the_cookie_and_a_second_one_writes_nothing()   // criterion 8
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        using var first = await RateLimitApi.SendAsync(Client, HttpMethod.Post, "/auth/logout", Remote, null, null);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);   // no cookie: no row
        Assert.Empty(await AuditAsync(AuditKinds.Logout));

        using var real = await SessionApi.Logout(Client, session.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, real.StatusCode);
        var row = await SingleAsync(AuditKinds.Logout);
        var seed = await Factory.SeedUserIdAsync();
        Assert.Equal(seed, row.ActorUserId);
        Assert.Equal(seed, row.SubjectUserId);

        using var again = await SessionApi.Logout(Client, session.RefreshToken);   // already revoked
        using var junk = await SessionApi.Logout(Client, "a-cookie-nobody-issued");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, junk.StatusCode);
        Assert.Single(await AuditAsync(AuditKinds.Logout));
    }

    // ---- password reset and email verification

    [Fact]
    public async Task A_reset_request_is_recorded_with_the_address_as_typed_and_no_account_and_only_when_queued()   // criterion 8
    {
        for (var i = 0; i < 5; i++)
        {
            Clock.Advance(PastTheGap);   // one accepted request per minute (spec 0004)
            using var response = await AccountApi.Forgot(Client, "Typed@Example.test");
            await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        }

        Clock.Advance(PastTheGap);
        using var limited = await AccountApi.Forgot(Client, "Typed@Example.test");   // the sixth within the hour: the mail limit
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        var rows = await AuditAsync(AuditKinds.PasswordResetRequested);
        Assert.Equal(5, rows.Count);   // a request that is not queued leaves no row
        Assert.All(rows, row =>
        {
            Assert.Equal("Typed@Example.test", row.SubjectEmail);   // as typed
            Assert.Null(row.SubjectUserId);   // no account: the request does not look one up
            Assert.Null(row.ActorUserId);
        });
    }

    [Fact]
    public async Task A_verification_request_writes_no_row()   // the contract has no kind for it
    {
        using var response = await AccountApi.RequestVerification(Client, "new@example.com");
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);

        Assert.Empty(await AuditAsync(AuditKinds.PasswordResetRequested));
        Assert.Empty(await AuditAsync(AuditKinds.EmailVerified));
    }

    [Fact]
    public async Task A_reset_is_recorded_without_the_link_or_the_password_and_a_failed_one_leaves_no_row()   // criteria 8 and 9
    {
        using (var forgot = await AccountApi.Forgot(Client, Factory.SeedEmail))
        {
            await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var token = TokenIn(Mail.Sent[^1]);

        using (var weak = await AccountApi.Reset(Client, token, "abc"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        }

        using (var unknown = await AccountApi.Reset(Client, "x".PadRight(43, 'x'), NewPassword))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }

        Assert.Empty(await AuditAsync(AuditKinds.PasswordReset));   // nothing changed, so no row

        using var done = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(done, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.PasswordReset);
        Assert.Equal(await Factory.SeedUserIdAsync(), row.SubjectUserId);
        Assert.Equal(Factory.SeedEmail, row.SubjectEmail);
        Assert.Null(row.ActorUserId);   // the person who holds the link is anonymous
        await AssertNoSecretsAsync(token, NewPassword, "abc", Factory.SeedPassword);
    }

    [Fact]
    public async Task A_verification_is_recorded_and_a_refused_one_leaves_no_row()   // criterion 8
    {
        var user = await CreateUserAsync("new@example.com", confirmed: false);
        using (var request = await AccountApi.RequestVerification(Client, "new@example.com"))
        {
            await AccountApi.AssertEmptyAsync(request, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var token = TokenIn(Mail.Sent[^1]);

        using (var invalid = await AccountApi.Verify(Client, "y".PadRight(43, 'y')))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        Assert.Empty(await AuditAsync(AuditKinds.EmailVerified));

        using var done = await AccountApi.Verify(Client, token);
        await AccountApi.AssertEmptyAsync(done, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.EmailVerified);
        Assert.Equal(user.Id, row.SubjectUserId);
        Assert.Equal("new@example.com", row.SubjectEmail);
        await AssertNoSecretsAsync(token);
    }
}
