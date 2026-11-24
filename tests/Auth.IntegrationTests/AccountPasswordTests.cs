using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → <c>POST /auth/account/password</c> (criteria 18 and 19), in the order of its checks, and Amendment A1.</summary>
public sealed class AccountPasswordTests : ProviderTestBase
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string OlaNormalized = "OLA@GMAIL.TEST";
    private const string NewPassword = "Fresh-Passw0rd-2";

    // Spelled out, so that the escape in a test body is not read as an escape of this file.
    private const char Backslash = (char)0x5C;

    // The hash read of the change is the only statement of these flows with this text ("FOR KEY SHARE" does not contain it).
    private const string HashRead = "FOR SHARE";

    private readonly ShareLockInterceptor _race = new();
    private readonly SessionRowFault _fault = new();

    public AccountPasswordTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(_race, _fault)));
    }

    private Task<int> AttemptsAsync() =>
        InDbAsync(db => db.LoginStreaks.Where(s => s.IdentifierHash == LoginIdentifier.HashOf(OlaNormalized)).Select(s => s.AttemptCount)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken));

    /// <summary>An account with a password and a Google login, signed in on this device; its window closed (the change never needs it).</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> AccountAsync()
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        Clock.Advance(TimeSpan.FromMinutes(6));
        return (user.Id, await SessionApi.RefreshOk(Client, session.RefreshToken));
    }

    /// <summary>The values of the one session whose authorization is still valid.</summary>
    private Task<SessionState> LiveSessionStateAsync() =>
        InDbAsync(db => (
            from state in db.SessionStates.AsNoTracking()
            join authorization in db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking() on state.AuthorizationId equals authorization.Id
            where authorization.Status == OpenIddictConstants.Statuses.Valid
            select state).SingleAsync(TestContext.Current.CancellationToken));

    private Task<int> LiveAuthorizationsAsync() =>
        InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
            .CountAsync(a => a.Status == OpenIddictConstants.Statuses.Valid, TestContext.Current.CancellationToken));

    /// <summary>A verification link of the account, as a second kind of link next to a reset's.</summary>
    private Task<int> AddVerificationLinkAsync(Guid userId) =>
        InDbAsync(db =>
        {
            db.EmailTokens.Add(new EmailToken
            {
                UserId = userId,
                Kind = MailKind.EmailVerification,
                TokenHash = EmailTokens.HashOf("a-verification-link-of-the-account"),
                ExpiresAt = StorableTime.Now(Clock) + TimeSpan.FromHours(24),
            });
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    private async Task<string> ResetTokenAsync()
    {
        using (var forgot = await AccountApi.Forgot(Client, Ola))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        await DispatchAsync();
        return TokenIn(Mail.Sent.Last(m => m.To == Ola));
    }

    [Fact]
    public async Task The_right_current_password_changes_it_and_this_device_alone_keeps_a_session()   // criterion 18
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        var userId = user.Id;
        var here = await SessionApi.LoginAsync(Client, Ola, UserPassword);   // the window of this session is open: the renewal must carry it over
        Clock.Advance(LockoutApi.HumanPace);
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        Clock.Advance(LockoutApi.HumanPace);
        await AddVerificationLinkAsync(userId);   // every link of the account, of either kind
        await ResetTokenAsync();
        await LockoutApi.FailAsync(Client, Clock, Ola, 1);
        var window = await SettingsApi.ConfirmedUntilAsync(Client, here);
        Assert.NotNull(window);   // an open window: two closed ones would be equal below whatever the renewal did
        var before = await InDbAsync(db => db.SessionStates.AsNoTracking().OrderBy(s => s.SessionStart).FirstAsync(TestContext.Current.CancellationToken));
        var stamp = await InDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken));

        using var response = await SettingsApi.ChangePassword(Client, here, UserPassword, NewPassword);

        var renewed = await SessionApi.ReadSessionAsync(response);
        Assert.Equal(userId.ToString(), AccessTokens.Text(renewed.AccessToken, "sub"));
        Assert.NotEqual(stamp, await InDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken)));
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, there.RefreshToken));
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, here.RefreshToken));
        Assert.Equal(window, await SettingsApi.ConfirmedUntilAsync(Client, renewed));   // the old window
        var live = await LiveSessionStateAsync();
        Assert.Equal((before.SessionStart, before.ConfirmedUntil), (live.SessionStart, live.ConfirmedUntil));   // and the old start
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == userId, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Single(await InDbAsync(db => db.UserLogins.Where(l => l.UserId == userId).ToListAsync(TestContext.Current.CancellationToken)));   // the logins stay

        var changed = await SingleAsync(AuditKinds.PasswordChanged);
        Assert.Equal((userId, userId, Ola), (changed.ActorUserId!.Value, changed.SubjectUserId!.Value, changed.SubjectEmail));
        Assert.Equal(await DevCompanyIdAsync(), changed.OrgId);
        Assert.Empty(AuditApi.DetailNames(changed));
        Assert.Equal("session_renewal", AuditApi.Text((await AuditAsync(AuditKinds.LoginSucceeded))[^1], "via"));
        Assert.Equal(1, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.PasswordChanged, TestContext.Current.CancellationToken)));

        // Afterwards the new password signs in and the old one does not.
        Clock.Advance(LockoutApi.HumanPace);
        using (var old = await LoginApi.Login(Client, Ola, UserPassword))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        }

        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, NewPassword);
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, renewed.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task An_open_window_alone_never_lets_it_act()   // criterion 18
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);   // the window is open
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, session));

        using var response = await SettingsApi.ChangePassword(Client, session, "Wrong-Passw0rd-9", NewPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        _ = user;
    }

    public static TheoryData<string> BadBodies => new()
    {
        "not json",
        "{}",
        """["current_password","new_password"]""",
        """{"current_password":"Another-Passw0rd"}""",
        """{"new_password":"Fresh-Passw0rd-2"}""",
        """{"current_password":"Another-Passw0rd","new_password":5}""",
        """{"current_password":"Another-Passw0rd","new_password":null}""",
        """{"current_password":"","new_password":"Fresh-Passw0rd-2"}""",
        """{"current_password":"  ","new_password":"Fresh-Passw0rd-2"}""",
        """{"current_password":5,"new_password":"Fresh-Passw0rd-2"}""",
        "{\"current_password\":\"Another-Passw0rd\",\"new_password\":\"Fresh" + Backslash + "u0000Passw0rd\"}",
        "{\"current_password\":\"Another" + Backslash + "u0000Passw0rd\",\"new_password\":\"Fresh-Passw0rd-2\"}",
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request_before_the_session_and_counts_for_nothing(string body)   // criterion 19, step 1
    {
        var (_, session) = await AccountAsync();

        using var response = await SettingsApi.PostRaw(Client, SettingsApi.PasswordPath, session with { RefreshToken = "" }, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, await AttemptsAsync());
    }

    [Fact]
    public async Task An_account_without_a_password_is_403_password_not_set_not_counted_and_nothing_written()   // criterion 19, step 3
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using var response = await SettingsApi.ChangePassword(Client, session, "Any-Passw0rd-1", "short");   // 3 before 4

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "password_not_set");
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
    }

    [Theory]
    [InlineData("short", new[] { "too_short", "requires_upper", "requires_digit" })]
    [InlineData("", new[] { "too_short", "requires_upper", "requires_lower", "requires_digit" })]
    public async Task A_new_password_that_breaks_the_policy_is_400_weak_password_with_its_rules_before_the_current_one_is_judged(string next, string[] rules)   // criterion 19, step 4
    {
        var (_, session) = await AccountAsync();

        using var response = await SettingsApi.ChangePassword(Client, session, "Wrong-Passw0rd-9", next);   // 4 before 5

        var body = await TenancyApi.ReadAsync(response, HttpStatusCode.BadRequest);
        using var json = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("weak_password", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(rules, json.RootElement.GetProperty("rules").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
    }

    [Fact]
    public async Task A_wrong_current_password_is_403_wrong_password_counted_with_account_confirm_refused()   // criterion 19, step 5
    {
        var (userId, session) = await AccountAsync();

        using var response = await SettingsApi.ChangePassword(Client, session, "Wrong-Passw0rd-9", NewPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(1, await AttemptsAsync());
        var row = await SingleAsync(AuditKinds.AccountConfirmRefused);
        Assert.Equal(userId, row.SubjectUserId);
        Assert.Equal(("wrong_password", "password"), (AuditApi.Text(row, "reason"), AuditApi.Text(row, "action")));
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, session.RefreshToken)).StatusCode);   // nothing ended
    }

    [Fact]
    public async Task After_ten_failures_it_is_429_too_many_attempts_with_account_confirm_refused_locked()   // criterion 19, step 5
    {
        var (_, session) = await AccountAsync();
        await LockoutApi.FailAsync(Client, Clock, Ola, 10);
        Clock.Advance(LockoutApi.HumanPace);

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);   // even the right one

        await LockoutApi.AssertLockedAsync(response);
        var row = (await AuditAsync(AuditKinds.AccountConfirmRefused))[^1];
        Assert.Equal(("locked", "password"), (AuditApi.Text(row, "reason"), AuditApi.Text(row, "action")));
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
    }

    [Fact]
    public async Task A_reset_that_used_its_link_up_first_wins_and_the_change_is_403_wrong_password_not_counted_again()   // criterion 19, Amendment A1, Review Focus 2
    {
        var (userId, session) = await AccountAsync();
        var token = await ResetTokenAsync();
        Task<HttpResponseMessage>? change = null;
        _race.Pattern = "FROM \"AspNetUsers\"";
        _race.Skip = 0;
        _race.Before = async () =>
        {
            // The reset holds its link (its first statement used it up) and is about to read the account: the change is sent now, passes
            // its checks and its re-reads under the address lock (the session is still alive), and waits for that link row.
            change = SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);
            await HeldLock.WaitForWaitersAsync(Factory, 1, change);
        };

        using (var reset = await AccountApi.Reset(Client, token, "Reset-Passw0rd-3"))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        using var response = await change!;

        Assert.True(_race.Fired);
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(0, await AttemptsAsync());   // counted once at step 5 and cleared there; not counted again
        var row = await SingleAsync(AuditKinds.AccountConfirmRefused);
        Assert.Equal(("wrong_password", "password"), (AuditApi.Text(row, "reason"), AuditApi.Text(row, "action")));
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        Assert.DoesNotContain(await AuditAsync(AuditKinds.LoginSucceeded), row => AuditApi.Text(row, "via") == "session_renewal");   // no renewal was issued
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.PasswordChanged, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == userId, TestContext.Current.CancellationToken)));
        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, "Reset-Passw0rd-3");   // the reset's password is the one; neither answered 500
    }

    [Fact]
    public async Task A_reset_that_committed_before_the_change_took_the_address_lock_ended_its_session_and_the_change_is_401_session_required()   // criterion 19, Amendment A1: the third outcome
    {
        var (_, session) = await AccountAsync();
        var token = await ResetTokenAsync();
        Task<HttpResponseMessage> change;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            change = SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);   // passes its checks and waits for the address lock
            await HeldLock.WaitForWaitersAsync(Factory, 1, change);
            using (var reset = await AccountApi.Reset(Client, token, "Reset-Passw0rd-3"))   // the reset takes no address lock: it commits first
            {
                Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
            }

            await held.ReleaseAsync();
        }

        using var response = await change;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");   // the reset ended every session, this one's too
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.PasswordChanged, TestContext.Current.CancellationToken)));
        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, "Reset-Passw0rd-3");   // the reset's password is the one; nothing answered 500
    }

    [Fact]
    public async Task A_failure_after_the_sessions_ended_rolls_the_whole_change_back()   // guard lesson 9: the sign-in's transaction owns the change
    {
        var (userId, here) = await AccountAsync();
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var stamp = await InDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken));
        await AddVerificationLinkAsync(userId);
        await ResetTokenAsync();   // a link of each kind
        await LockoutApi.FailAsync(Client, Clock, Ola, 1);
        Clock.Advance(LockoutApi.HumanPace);
        _fault.Armed = true;

        using var failed = await SettingsApi.ChangePassword(Client, here, UserPassword, NewPassword);

        Assert.True(_fault.Fired, "The renewed session never reached its write.");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.False(failed.Headers.Contains("Set-Cookie"));   // the refresh cookie is not replaced
        Assert.Equal(stamp, await InDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(2, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == userId, TestContext.Current.CancellationToken)));   // the links are still there
        Assert.Equal(0, await AttemptsAsync());   // cleared where the right password was recognised, on a connection of its own: the rollback does not bring it back
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.PasswordChanged, TestContext.Current.CancellationToken)));
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, there.RefreshToken)).StatusCode);   // no session was revoked
        _fault.Armed = false;
        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, UserPassword);   // the old password still signs in
    }

    [Fact]
    public async Task A_change_that_voided_the_links_first_wins_and_the_reset_is_400_invalid_token()   // criterion 19, Amendment A1, Review Focus 2
    {
        var (_, session) = await AccountAsync();
        var token = await ResetTokenAsync();
        Task<HttpResponseMessage>? reset = null;
        _race.Pattern = HashRead;
        _race.Skip = 0;
        _race.Before = async () =>
        {
            // The change has deleted the account's links and is about to read its hash: the reset now waits for the change's delete.
            reset = AccountApi.Reset(Client, token, "Reset-Passw0rd-3");
            await HeldLock.WaitForWaitersAsync(Factory, 1, reset);
        };

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_race.Fired);
        using var late = await reset!;
        await AccountApi.AssertErrorAsync(late, """{"error":"invalid_token"}""");
        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, NewPassword);   // the change's password is the one; neither answered 500
    }

    [Fact]
    public async Task Two_changes_at_once_give_one_200_and_one_401_session_required()   // criterion 19, with parallel requests
    {
        var (_, session) = await AccountAsync();

        Task<HttpResponseMessage>[] pending;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            pending = [SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword), SettingsApi.ChangePassword(Client, session, UserPassword, "Other-Passw0rd-4")];
            await HeldLock.WaitForWaitersAsync(Factory, 2, pending);
            await held.ReleaseAsync();
        }

        var answers = await Task.WhenAll(pending);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Unauthorized], answers.Select(a => a.StatusCode).Order());
        await TenancyApi.AssertErrorAsync(answers.Single(a => a.StatusCode == HttpStatusCode.Unauthorized), HttpStatusCode.Unauthorized, "session_required");
        Assert.Single(await AuditAsync(AuditKinds.PasswordChanged));
        foreach (var answer in answers)
        {
            answer.Dispose();
        }
    }

    [Fact]
    public async Task Without_the_refresh_cookie_it_is_401_session_required_and_without_a_token_the_plain_401()   // criterion 19
    {
        var (_, session) = await AccountAsync();

        using var noCookie = await SettingsApi.PostAs(
            Client, SettingsApi.PasswordPath, session.AccessToken, null, new { current_password = UserPassword, new_password = NewPassword });
        using var noToken = await SettingsApi.PostAs(
            Client, SettingsApi.PasswordPath, null, session.RefreshToken, new { current_password = UserPassword, new_password = NewPassword });

        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");
        await TenancyApi.AssertUnauthorizedAsync(noToken);
        Assert.Equal(0, await AttemptsAsync());
    }

    [Fact]
    public async Task A_removed_membership_is_403_no_membership_and_nothing_is_counted()   // criterion 19
    {
        var (userId, session) = await AccountAsync();
        await RemoveMembershipAsync(userId);

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
    }

    [Fact]
    public async Task A_session_past_its_cap_is_401_session_required_and_nothing_changes()   // spec 0012 → Sessions
    {
        var (userId, session) = await AccountAsync();
        await InDbAsync(db => db.SessionStates.ExecuteUpdateAsync(
            s => s.SetProperty(state => state.SessionStart, StorableTime.Now(Clock) - TimeSpan.FromDays(31)), TestContext.Current.CancellationToken));

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, UserPassword);   // the password did not change
        _ = userId;
    }

    [Fact]
    public async Task No_password_token_or_cookie_reaches_the_audit_log_or_the_log()   // guard lesson 4
    {
        var (_, session) = await AccountAsync();
        using (var wrong = await SettingsApi.ChangePassword(Client, session, "Wrong-Passw0rd-9", NewPassword))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        }

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);
        var renewed = await SessionApi.ReadSessionAsync(response);

        string[] secrets = ["Wrong-Passw0rd-9", UserPassword, NewPassword, session.RefreshToken, renewed.RefreshToken, renewed.AccessToken];
        await AssertNoSecretsAsync(secrets);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal));
    }

    // ---- The checks in the order of the contract, per adjacent pair ----

    [Fact]
    public async Task The_session_and_the_membership_are_judged_before_the_password_is_missed()   // criterion 19: 2 before 3
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using var noCookie = await SettingsApi.PostAs(
            Client, SettingsApi.PasswordPath, session.AccessToken, null, new { current_password = UserPassword, new_password = NewPassword });
        await RemoveMembershipAsync(userId);
        using var noMembership = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");   // not password_not_set
        await TenancyApi.AssertErrorAsync(noMembership, HttpStatusCode.Forbidden, "no_membership");
    }

    [Fact]
    public async Task A_locked_identifier_judges_the_policy_first_and_a_weak_password_is_neither_counted_nor_refused_as_locked()   // criterion 19: 4 before 5
    {
        var (_, session) = await AccountAsync();
        await LockoutApi.FailAsync(Client, Clock, Ola, 10);
        Clock.Advance(LockoutApi.HumanPace);

        using var weak = await SettingsApi.ChangePassword(Client, session, UserPassword, "short");

        var body = await TenancyApi.ReadAsync(weak, HttpStatusCode.BadRequest);
        Assert.Contains("weak_password", body, StringComparison.Ordinal);
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.Equal(10, await AttemptsAsync());
    }

    // ---- The checks made again under the address lock ----

    [Fact]
    public async Task A_membership_removed_while_the_request_waits_for_the_lock_is_403_no_membership_and_nothing_is_written()
    {
        var (userId, session) = await AccountAsync();

        Task<HttpResponseMessage> change;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            change = SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);
            await HeldLock.WaitForWaitersAsync(Factory, 1, change);
            await RemoveMembershipAsync(userId);
            await held.ReleaseAsync();
        }

        using var response = await change;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.PasswordChanged, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await LiveAuthorizationsAsync());   // nothing ended
    }

    [Fact]
    public async Task A_password_removed_while_the_request_waits_for_the_lock_is_403_password_not_set_and_nothing_is_written()
    {
        var (userId, session) = await AccountAsync();
        await ResetTokenAsync();

        Task<HttpResponseMessage> change;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            change = SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);
            await HeldLock.WaitForWaitersAsync(Factory, 1, change);
            await InDbAsync(db => db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(
                s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
            await held.ReleaseAsync();
        }

        using var response = await change;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "password_not_set");
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        Assert.Equal(1, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == userId, TestContext.Current.CancellationToken)));   // the link is still there
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, session.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task An_account_deleted_while_the_request_waits_for_the_lock_is_401_session_required()
    {
        var (userId, session) = await AccountAsync();

        Task<HttpResponseMessage> change;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            change = SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);
            await HeldLock.WaitForWaitersAsync(Factory, 1, change);
            await held.DeleteAccountAsync(userId);
            await held.ReleaseAsync();
        }

        using var response = await change;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
    }

    [Fact]
    public async Task A_session_whose_cap_ends_while_the_request_waits_for_the_lock_is_401_session_required_and_nothing_changes()   // clock read after the lock
    {
        var (_, session) = await AccountAsync();
        await InDbAsync(db => db.SessionStates.ExecuteUpdateAsync(
            s => s.SetProperty(state => state.SessionStart, StorableTime.Now(Clock) - TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1)),
            TestContext.Current.CancellationToken));

        Task<HttpResponseMessage> change;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            change = SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);   // the cap is a minute away when it is sent
            await HeldLock.WaitForWaitersAsync(Factory, 1, change);
            Clock.Advance(TimeSpan.FromMinutes(2));
            await held.ReleaseAsync();
        }

        using var response = await change;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, UserPassword);   // the password did not change
    }

    // ---- No lock cycle with the flows that take no address lock ----

    /// <summary>
    /// Stops the next request before the command with <see cref="HashRead"/> (the change has deleted the account's links by then and is about
    /// to read its hash), sends <paramref name="step"/> then and lets the change go on only once the step's request waits for a lock.
    /// </summary>
    private Func<Task<HttpResponseMessage>> SendWhileTheChangeHoldsTheLinks(Func<Task<HttpResponseMessage>> step)
    {
        Task<HttpResponseMessage>? sent = null;
        _race.Pattern = HashRead;
        _race.Skip = 0;
        _race.Before = async () =>
        {
            sent = step();
            await HeldLock.WaitForWaitersAsync(Factory, 1, sent);
        };
        return () => sent ?? throw new InvalidOperationException("The change never reached the command it was to be stopped before.");
    }

    [Fact]
    public async Task A_password_change_and_an_unlink_renew_the_session_in_the_company_it_was_switched_to()   // D48: the renewal keeps the caller's company
    {
        var (userId, session) = await AccountAsync();
        var other = await CreateCompanyAsync("Second");
        await AddMembershipAsync(userId, other, "user");
        var switched = await SessionApi.SwitchOk(Client, session.RefreshToken, other);
        Assert.Equal(other.ToString(), AccessTokens.Text(switched.AccessToken, "org_id"));
        using (var confirmed = await SettingsApi.Confirm(Client, switched, new { password = UserPassword }))   // the unlink needs an open window
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        var before = await LiveSessionStateAsync();

        using var changed = await SettingsApi.ChangePassword(Client, switched, UserPassword, NewPassword);
        var afterChange = await SessionApi.ReadSessionAsync(changed);
        using var unlinked = await SettingsApi.Unlink(Client, afterChange, "google");
        var afterUnlink = await SessionApi.ReadSessionAsync(unlinked);

        Assert.Equal(other.ToString(), AccessTokens.Text(afterChange.AccessToken, "org_id"));
        Assert.Equal(other.ToString(), AccessTokens.Text(afterUnlink.AccessToken, "org_id"));
        var live = await LiveSessionStateAsync();
        Assert.Equal((before.SessionStart, before.ConfirmedUntil), (live.SessionStart, live.ConfirmedUntil));   // the same start and window
    }

    [Fact]
    public async Task An_account_removed_by_a_member_removal_that_commits_before_the_change_reads_its_row_is_401_session_required_and_nothing_is_written()
    {
        var (company, _, adminToken) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Ola, "user");
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        await ResetTokenAsync();
        _race.Pattern = "FOR NO KEY UPDATE";   // the first such command of the change is the lock of its membership row (spec 0015's order)
        _race.Skip = 0;
        _race.Before = async () =>
        {
            // The removal takes no address lock: it deletes the account and commits while the change, past its re-reads, is about to lock its membership row.
            using var removed = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", adminToken);
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        };

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        Assert.True(_race.Fired);
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.PasswordChanged, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_member_removal_that_arrives_while_the_change_holds_the_links_and_the_change_neither_deadlock()   // lock order: the account's row before the links
    {
        var (company, _, adminToken) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Ola, "user");
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        await ResetTokenAsync();   // a link to delete: the removal's account delete cascades to it
        var removal = SendWhileTheChangeHoldsTheLinks(() => TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", adminToken));

        using var changed = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        Assert.True(_race.Fired);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var removed = await removal();
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_company_deletion_that_arrives_while_the_change_holds_the_links_and_the_change_neither_deadlock()   // lock order: the account's row before the links
    {
        var (company, _, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var worker = await AddMemberAsync(company, Ola, "user");
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        await ResetTokenAsync();
        var deletion = SendWhileTheChangeHoldsTheLinks(
            () => TenancyApi.Send(Client, HttpMethod.Delete, "/auth/org", adminToken, new { name = "Acme", password = UserPassword }));

        using var changed = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        Assert.True(_race.Fired);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var deleted = await deletion();
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_self_deletion_that_arrives_while_the_change_holds_the_links_waits_for_the_address_lock_and_is_neither_a_deadlock_nor_a_500()
    {
        var (userId, _) = await CreatePrivateAccountAsync(Ola);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        await ResetTokenAsync();
        var deletion = SendWhileTheChangeHoldsTheLinks(() => ProviderApi.DeleteAccount(Client, session.AccessToken, new { password = UserPassword }, null));

        using var changed = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        Assert.True(_race.Fired);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var deleted = await deletion();
        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);   // it judged the old password after the change: a wrong one now
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Id == userId, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_reset_that_holds_its_link_and_the_change_neither_deadlock_when_the_account_has_both_kinds_of_link()   // the change takes the reset's kind first
    {
        var (userId, session) = await AccountAsync();
        await AddVerificationLinkAsync(userId);   // older than the reset's link, in the order a scan of the table meets them: the verification link first
        var token = await ResetTokenAsync();
        Task<HttpResponseMessage>? change = null;
        _race.Pattern = "FROM \"AspNetUsers\"";
        _race.Skip = 0;
        _race.Before = async () =>
        {
            change = SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);
            await HeldLock.WaitForWaitersAsync(Factory, 1, change);
        };

        using (var reset = await AccountApi.Reset(Client, token, "Reset-Passw0rd-3"))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);   // not a 500: it does not wait for a link that the change holds
        }

        using var response = await change!;

        Assert.True(_race.Fired);
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == userId, TestContext.Current.CancellationToken)));
    }
}
