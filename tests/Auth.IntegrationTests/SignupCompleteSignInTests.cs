using System.Net;
using System.Text.Json;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0009, Amendment 2: completing a sign-up signs the person in, and answers as a successful <c>POST /auth/login</c> does. The sign-in is part of the
/// completion: if it cannot be written, nothing of the completion stays.
/// </summary>
public sealed class SignupCompleteSignInTests : SignupTestBase
{
    // While true, a handler of the test refuses every sign-in that OpenIddict is asked for: the completion must then fail as a whole.
    private bool refusing;

    private const string Mixed = "Anna@Firma.test";
    private const string WeakRules = """{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}""";

    // Triggers that make the database refuse one write, to fail the sign-in after the completion has done its own work.
    private const string RefuseEverything =
        "CREATE FUNCTION refuse_row() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'refused'; END $$ LANGUAGE plpgsql";
    private const string RefuseLoginSucceeded =
        "CREATE FUNCTION refuse_row() RETURNS trigger AS $$ BEGIN IF NEW.kind = 'login.succeeded' THEN RAISE EXCEPTION 'refused'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql";
    private const string RefuseTokens =
        """CREATE TRIGGER refuse_row BEFORE INSERT ON "OpenIddictTokens" FOR EACH ROW EXECUTE FUNCTION refuse_row()""";
    private const string RefuseAudit =
        "CREATE TRIGGER refuse_row BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION refuse_row()";
    private const string AllowTokens = """DROP TRIGGER refuse_row ON "OpenIddictTokens" """;
    private const string AllowAudit = "DROP TRIGGER refuse_row ON audit_events";

    // The claims of an access token as LoginTests pins them: the contract, OpenIddict's own metadata, the three of spec 0005 and org_kind of spec 0010.
    private static readonly string[] AccessTokenClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"];

    public SignupCompleteSignInTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.Configure<OpenIddictServerOptions>(options => options.Handlers.Add(
            OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
                .UseInlineHandler(context =>
                {
                    if (refusing)
                    {
                        // A client-class error: OpenIddict would answer it with a 400 of its own, and the completion must still end as the 500 of the contract.
                        context.Reject(OpenIddictConstants.Errors.InvalidGrant, "Refused by the test.");
                    }

                    return ValueTask.CompletedTask;
                })
                .SetOrder(OpenIddictServerHandlers.EvaluateGeneratedTokens.Descriptor.Order + 600)   // after the handlers that decide the tokens, before any is made
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build())));
    }

    private Task<long> TokenRowsAsync() =>
        InDbAsync(db => db.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM "OpenIddictTokens" """).SingleAsync(TestContext.Current.CancellationToken));

    private Task<long> AuthorizationRowsAsync() =>
        InDbAsync(db => db.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM "OpenIddictAuthorizations" """).SingleAsync(TestContext.Current.CancellationToken));

    private Task<Guid> CompanyIdAsync() =>
        InDbAsync(db => db.Companies.Where(c => c.Name == CompanyName).Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken));

    /// <summary>The completion failed halfway: <c>500</c>, no cookie, nothing of it stays, and the link is as it was.</summary>
    private async Task AssertNothingStaysAsync(HttpResponseMessage failed, string token)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("""{"error":"internal_error"}""", await failed.Content.ReadAsStringAsync());   // the contract's one 500, whatever refused
        Assert.False(failed.Headers.Contains("Set-Cookie"), "A completion that failed must not write the cookie.");
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Address, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));   // only the development company
        Assert.Equal(0L, await TokenRowsAsync());
        Assert.Equal(0L, await AuthorizationRowsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.SignupCompleted));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
        Assert.DoesNotContain(await AuditAsync(AuditKinds.OrgCreated), row => row.OrgName == CompanyName);
        var pending = Assert.Single(await PendingAsync());
        Assert.Equal(EmailTokens.HashOf(token), pending.TokenHash);   // the use of the link was rolled back with the rest
    }

    [Fact]
    public async Task The_completion_answers_as_a_login_does_with_the_status_and_the_access_token_only()   // criterion 9
    {
        var token = await MailedAsync(Mixed);

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);

        var raw = await done.Content.ReadAsStringAsync();
        Assert.True(done.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)done.StatusCode}: {raw}");
        Assert.Equal("application/json", done.Content.Headers.ContentType?.MediaType);
        Assert.True(done.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");
        Assert.Contains(done.Headers.Pragma, p => p.Name == "no-cache");
        using var body = JsonDocument.Parse(raw);
        Assert.Equal(["access_token", "status"], body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("authenticated", body.RootElement.GetProperty("status").GetString());
        var cookie = SessionApi.RefreshCookieOf(done);
        Assert.DoesNotContain(cookie.Value.Value!, raw, StringComparison.Ordinal);   // the refresh token never reaches a body
    }

    [Fact]
    public async Task The_cookie_and_the_headers_are_those_of_a_login()   // criterion 9
    {
        var token = await MailedAsync(Mixed);

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        using var login = await LoginApi.Login(Client, Mixed, Password);

        Assert.Single(done.Headers.GetValues("Set-Cookie"));
        var cookie = SessionApi.RefreshCookieOf(done);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.Equal("/auth", cookie.Path.Value);
        Assert.Equal(TimeSpan.FromDays(14), cookie.MaxAge);
        Assert.False(cookie.Domain.HasValue);
        Assert.Null(cookie.Expires);

        var reference = SessionApi.RefreshCookieOf(login);
        Assert.Equal(reference.HttpOnly, cookie.HttpOnly);
        Assert.Equal(reference.Secure, cookie.Secure);
        Assert.Equal(reference.SameSite, cookie.SameSite);
        Assert.Equal(reference.Path, cookie.Path);
        Assert.Equal(reference.MaxAge, cookie.MaxAge);
        Assert.NotEqual(reference.Value.Value, cookie.Value.Value);   // a session of its own
        Assert.Equal(LoginApi.HeaderNames(login), LoginApi.HeaderNames(done));
    }

    [Fact]
    public async Task The_token_carries_the_new_company_and_the_first_admins_role_and_the_claims_of_a_login()   // criterion 9
    {
        var token = await MailedAsync(Mixed);

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(done);

        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken));
        var company = await CompanyIdAsync();
        var jwt = new JsonWebToken(session.AccessToken);
        Assert.Equal(AccessTokenClaims, jwt.Claims.Select(c => c.Type).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal("at+jwt", jwt.Typ);
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(
            ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            AccessTokens.Array(session.AccessToken, "permissions"));

        // A login a moment later says the same.
        var later = await SessionApi.LoginAsync(Client, Mixed, Password);
        var laterJwt = new JsonWebToken(later.AccessToken);
        Assert.Equal(jwt.Subject, laterJwt.Subject);
        Assert.Equal(jwt.Audiences, laterJwt.Audiences);
        Assert.Equal(jwt.Issuer, laterJwt.Issuer);
        Assert.Equal(AccessTokens.Text(session.AccessToken, "org_id"), AccessTokens.Text(later.AccessToken, "org_id"));
        Assert.Equal(AccessTokens.Array(session.AccessToken, "roles"), AccessTokens.Array(later.AccessToken, "roles"));
        Assert.Equal(AccessTokens.Array(session.AccessToken, "permissions"), AccessTokens.Array(later.AccessToken, "permissions"));
    }

    [Fact]
    public async Task The_cookie_refreshes_like_a_logins_and_belongs_to_a_family()   // criterion 9
    {
        var token = await MailedAsync();

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(done);

        using (var scope = Factory.Services.CreateScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var entry = await tokens.FindByReferenceIdAsync(session.RefreshToken);
            Assert.NotNull(entry);
            Assert.Equal(OpenIddictConstants.TokenTypeIdentifiers.RefreshToken, await tokens.GetTypeAsync(entry));
            Assert.False(string.IsNullOrEmpty(await tokens.GetAuthorizationIdAsync(entry)), "The refresh token has no authorization (family).");
        }

        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Assert.Equal(AccessTokens.Text(session.AccessToken, "org_id"), AccessTokens.Text(refreshed.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(refreshed.AccessToken, "roles"));
        Assert.NotEqual(session.RefreshToken, refreshed.RefreshToken);   // rotated

        using var loggedOut = await SessionApi.Logout(Client, refreshed.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        using var after = await SessionApi.Refresh(Client, refreshed.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(after);   // the family ended with the logout
    }

    [Fact]
    public async Task The_session_starts_at_the_completion_and_ends_at_the_cap_of_a_login()   // spec 0002: the absolute cap counts from the start of the session
    {
        var token = await MailedAsync();
        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(done);

        Clock.Advance(TimeSpan.FromDays(13));
        var day13 = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Clock.Advance(TimeSpan.FromDays(13));
        var day26 = await SessionApi.RefreshOk(Client, day13.RefreshToken);
        Assert.InRange(day26.Cookie.MaxAge!.Value, TimeSpan.FromDays(4) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(4));   // 4 days to the cap

        Clock.Advance(TimeSpan.FromDays(4) + TimeSpan.FromMinutes(1));
        using var response = await SessionApi.Refresh(Client, day26.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(response);
    }

    [Fact]
    public async Task The_session_carries_the_stamp_of_the_account_so_a_password_change_ends_it()   // spec 0004 -> Effects of a reset
    {
        var token = await MailedAsync();
        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(done);

        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var account = await users.FindByEmailAsync(Address);
            Assert.NotNull(account);
            Assert.True((await users.UpdateSecurityStampAsync(account)).Succeeded);
        }

        using var refreshed = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refreshed);
    }

    [Fact]
    public async Task The_completion_writes_login_succeeded_beside_signup_completed_and_org_created()   // criterion 19, Amendment 2
    {
        var token = await MailedAsync(Mixed);

        using var done = await RateLimitApi.SendAsync(
            Client, HttpMethod.Post, SignupApi.CompletePath, CallerAddress, null,
            $$"""{"token":"{{token}}","password":"{{Password}}","company_name":"{{CompanyName}}"}""");
        var session = await SessionApi.ReadSessionAsync(done);

        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken));
        var company = await CompanyIdAsync();
        var signedIn = await SingleAsync(AuditKinds.LoginSucceeded);
        Assert.Equal(user.Id, signedIn.ActorUserId);
        Assert.Equal(user.Id, signedIn.SubjectUserId);
        Assert.Equal(Mixed, signedIn.SubjectEmail);
        Assert.Equal(company, signedIn.OrgId);
        Assert.Equal(CompanyName, signedIn.OrgName);
        Assert.Equal(CallerAddress, signedIn.ClientIp);
        Assert.Null(signedIn.TargetId);

        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        var created = Assert.Single(await AuditAsync(AuditKinds.OrgCreated), row => row.OrgId == company);
        Assert.Equal("signup", AuditApi.Text(created, "via"));
        Assert.Equal(completed.OrgId, signedIn.OrgId);
        Assert.Empty(await AuditAsync(AuditKinds.LoginFailed));
        await AssertNoSecretsAsync(Password, token, session.AccessToken, session.RefreshToken);
    }

    [Fact]
    public async Task A_token_store_that_refuses_leaves_nothing_of_the_completion_and_the_link_still_works()   // Amendment 2: the sign-in is part of the completion
    {
        var token = await MailedAsync();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseEverything, TestContext.Current.CancellationToken));
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseTokens, TestContext.Current.CancellationToken));

        using (var failed = await SignupApi.Complete(Client, token, Password, CompanyName))
        {
            await AssertNothingStaysAsync(failed, token);
        }

        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(AllowTokens, TestContext.Current.CancellationToken));
        using var again = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(again);
        Assert.NotEmpty(session.AccessToken);
    }

    [Fact]
    public async Task An_audit_log_that_refuses_the_sign_in_leaves_nothing_of_the_completion_and_the_link_still_works()   // Amendment 2: the sign-in is part of the completion
    {
        var token = await MailedAsync();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseLoginSucceeded, TestContext.Current.CancellationToken));
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseAudit, TestContext.Current.CancellationToken));

        using (var failed = await SignupApi.Complete(Client, token, Password, CompanyName))
        {
            await AssertNothingStaysAsync(failed, token);   // the tokens were issued first and went with the rollback
        }

        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(AllowAudit, TestContext.Current.CancellationToken));
        using var again = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(again);
        Assert.NotEmpty(session.AccessToken);
        Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task A_sign_in_that_OpenIddict_refuses_leaves_nothing_of_the_completion_and_the_link_still_works()   // Amendment 2: an error of OpenIddict is not an answer that may leave the account behind
    {
        var token = await MailedAsync();
        refusing = true;

        using (var failed = await SignupApi.Complete(Client, token, Password, CompanyName))
        {
            await AssertNothingStaysAsync(failed, token);   // OpenIddict's refusal would be a 400; here it is the 500, no cookie, and nothing of the completion
        }

        refusing = false;
        using var again = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(again);
        Assert.NotEmpty(session.AccessToken);
    }

    [Fact]
    public async Task The_same_body_posted_again_is_invalid_token_with_no_cookie_and_no_second_session()   // criterion 12
    {
        var token = await MailedAsync();
        using (var first = await SignupApi.Complete(Client, token, Password, CompanyName))
        {
            _ = await SessionApi.ReadSessionAsync(first);
        }

        var tokens = await TokenRowsAsync();
        var authorizations = await AuthorizationRowsAsync();
        Assert.Equal(1L, authorizations);   // the one session of the first post

        using (var second = await SignupApi.Complete(Client, token, Password, CompanyName))
        {
            await AccountApi.AssertErrorAsync(second, AccountApi.InvalidToken);   // never stored, and no cookie
        }

        Assert.Equal(tokens, await TokenRowsAsync());
        Assert.Equal(authorizations, await AuthorizationRowsAsync());
        Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task Every_refusal_is_what_it_was_and_issues_no_cookie_and_no_token()   // criteria 11, 12, 14
    {
        const string Taken = "taken@firma.test";
        var token = await MailedAsync();
        await CreateUserAsync(Taken, confirmed: true);
        var takenToken = await PendingSignupSeed.IssueAsync(Factory.Services, Taken, CompanyName);

        using (var weak = await SignupApi.Complete(Client, token, "short", CompanyName))
        {
            await AccountApi.AssertErrorAsync(weak, WeakRules);   // 400 with the rules, never stored, no cookie
        }

        using (var unknown = await SignupApi.Complete(Client, EmailTokens.NewToken(), Password, CompanyName))
        {
            await AccountApi.AssertErrorAsync(unknown, AccountApi.InvalidToken);
        }

        using (var malformed = await AccountApi.PostRaw(Client, SignupApi.CompletePath, """{"token":"x"}"""))
        {
            await AccountApi.AssertErrorAsync(malformed, AccountApi.InvalidRequest);
        }

        using (var registered = await SignupApi.Complete(Client, takenToken, Password, CompanyName))
        {
            await SignupApi.AssertAlreadyRegisteredAsync(registered);
        }

        Assert.Equal(0L, await TokenRowsAsync());
        Assert.Equal(0L, await AuthorizationRowsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task A_form_or_a_credentials_body_is_refused_with_400_and_issues_nothing()   // the endpoint's own body rules still apply on the pass-through path
    {
        using var form = await Client.PostAsync(
            SignupApi.CompletePath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = Factory.SeedEmail,
                ["password"] = Factory.SeedPassword,
            }));
        await AccountApi.AssertErrorAsync(form, AccountApi.InvalidRequest);

        using var credentials = await AccountApi.PostRaw(
            Client, SignupApi.CompletePath, JsonSerializer.Serialize(new { email = Factory.SeedEmail, password = Factory.SeedPassword }));
        await AccountApi.AssertErrorAsync(credentials, AccountApi.InvalidRequest);

        Assert.Equal(0L, await TokenRowsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task What_a_client_adds_to_a_valid_completion_never_reaches_the_OpenIddict_request()   // the pass-through request is a placeholder: no second way to log in or to refresh
    {
        var seed = await SessionApi.LoginAsync(Client, Factory);
        var seedSubject = new JsonWebToken(seed.AccessToken).Subject;
        var token = await MailedAsync(Mixed);

        // A valid completion body that also names the seed account, a password grant and the seed's refresh token, with the seed's cookie attached.
        var body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["token"] = token,
            ["password"] = Password,
            ["company_name"] = CompanyName,
            ["email"] = Factory.SeedEmail,
            ["username"] = Factory.SeedEmail,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = seed.RefreshToken,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, SignupApi.CompletePath)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Cookie", $"{SessionApi.CookieName}={seed.RefreshToken}");

        using var done = await Client.SendAsync(request);
        var session = await SessionApi.ReadSessionAsync(done);

        var created = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken));
        var subject = new JsonWebToken(session.AccessToken).Subject;
        Assert.Equal(created.Id.ToString(), subject);   // the new account, not the seed's
        Assert.NotEqual(seedSubject, subject);
        Assert.NotEqual(seed.RefreshToken, session.RefreshToken);

        // The seed's refresh token was neither redeemed nor rotated by the completion.
        var refreshed = await SessionApi.RefreshOk(Client, seed.RefreshToken);
        Assert.Equal(seedSubject, new JsonWebToken(refreshed.AccessToken).Subject);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Other_methods_are_still_405(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), SignupApi.CompletePath);

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
