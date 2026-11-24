using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → <c>POST /auth/account/confirm</c> (criteria 10 and 11), in the order of its checks.</summary>
public sealed class AccountConfirmTests : ProviderTestBase
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string OlaNormalized = "OLA@GMAIL.TEST";

    // Spelled out, so that the escape in a test body is not read as an escape of this file.
    private const char Backslash = (char)0x5C;

    private readonly OldRefreshTokens _oldTokens;

    public AccountConfirmTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        _oldTokens = OldRefreshTokens.Install(Factory);
    }

    private Task<int> AttemptsAsync() =>
        InDbAsync(db => db.LoginStreaks.Where(s => s.IdentifierHash == LoginIdentifier.HashOf(OlaNormalized)).Select(s => s.AttemptCount)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken));

    private string OpenUntil() => SettingsApi.Window(StorableTime.Now(Clock) + ConfirmationWindow.Lifetime);

    /// <summary>An account with a password, signed in on this device, its window closed.</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> PasswordAccountAsync()
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        Clock.Advance(TimeSpan.FromMinutes(6));
        return (user.Id, session);
    }

    /// <summary>A private account made with Google (no password), signed in on this device, its window closed.</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> PasswordlessAccountAsync()
    {
        var made = await PrivateThroughProviderAsync("google", GooglePerson());
        Clock.Advance(TimeSpan.FromMinutes(6));
        return made;
    }

    private async Task FailConfirmsAsync(SessionApi.Session session, int times)
    {
        for (var i = 0; i < times; i++)
        {
            Clock.Advance(LockoutApi.HumanPace);
            using var response = await SettingsApi.Confirm(Client, session, new { password = LockoutApi.WrongPassword });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>Whether the window of the only session is open at the test clock, read from the table.</summary>
    private async Task<bool> WindowIsOpenAsync()
    {
        var until = (await InDbAsync(db => db.SessionStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken))).ConfirmedUntil;
        return until is { } end && end > StorableTime.Now(Clock);
    }

    [Fact]
    public async Task The_right_password_opens_the_window_writes_account_confirmed_and_clears_the_streak()   // criterion 10
    {
        var (userId, session) = await PasswordAccountAsync();
        await FailConfirmsAsync(session, 2);
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));

        using var response = await SettingsApi.Confirm(Client, session, new { password = UserPassword });

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.False(response.Headers.Contains("Set-Cookie"));   // a password confirmation sets and clears no cookie
        Assert.Equal(OpenUntil(), await SettingsApi.ConfirmedUntilAsync(Client, session));
        Assert.Equal(0, await AttemptsAsync());
        var row = await SingleAsync(AuditKinds.AccountConfirmed);
        Assert.Equal((userId, userId, Ola), (row.ActorUserId!.Value, row.SubjectUserId!.Value, row.SubjectEmail));
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal(["method"], AuditApi.DetailNames(row));
        Assert.Equal("password", AuditApi.Text(row, "method"));
    }

    [Fact]
    public async Task A_wrong_password_is_403_wrong_password_counted_with_account_confirm_refused()   // criterion 10
    {
        var (userId, session) = await PasswordAccountAsync();

        using var response = await SettingsApi.Confirm(Client, session, new { password = "Wrong-Passw0rd-9" });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(1, await AttemptsAsync());
        var row = await SingleAsync(AuditKinds.AccountConfirmRefused);
        Assert.Equal(userId, row.SubjectUserId);
        Assert.Equal(["action", "reason"], AuditApi.DetailNames(row));
        Assert.Equal(("wrong_password", "confirm"), (AuditApi.Text(row, "reason"), AuditApi.Text(row, "action")));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
    }

    [Fact]
    public async Task After_ten_failures_it_is_429_too_many_attempts_with_account_confirm_refused_locked()   // criterion 10
    {
        var (_, session) = await PasswordAccountAsync();
        await FailConfirmsAsync(session, 10);
        Clock.Advance(LockoutApi.HumanPace);

        using var locked = await SettingsApi.Confirm(Client, session, new { password = UserPassword });   // even the right one

        await LockoutApi.AssertLockedAsync(locked);
        var rows = await AuditAsync(AuditKinds.AccountConfirmRefused);
        Assert.Equal("locked", AuditApi.Text(rows[^1], "reason"));
        Assert.Equal("confirm", AuditApi.Text(rows[^1], "action"));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
    }

    [Fact]
    public async Task Failures_at_the_confirmation_and_at_the_login_share_one_streak()   // spec 0012 → Limits: counted as at DELETE /auth/account
    {
        var (_, session) = await PasswordAccountAsync();
        await FailConfirmsAsync(session, 5);
        await LockoutApi.FailAsync(Client, Clock, Ola, 5);

        using var locked = await LoginApi.Login(Client, Ola, UserPassword);

        await LockoutApi.AssertLockedAsync(locked);
    }

    [Fact]
    public async Task A_reauth_ticket_of_a_linked_identity_uses_it_up_clears_auth_pt_and_writes_account_confirmed_with_the_provider()   // criterion 11
    {
        var (userId, session) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await SettingsApi.ConfirmWithTicket(Client, session, ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal(0, await TicketCountAsync());
        Assert.Equal(OpenUntil(), await SettingsApi.ConfirmedUntilAsync(Client, session));
        var row = await SingleAsync(AuditKinds.AccountConfirmed);
        Assert.Equal((userId, "google"), (row.SubjectUserId!.Value, AuditApi.Text(row, "method")));
        Assert.Equal(["method"], AuditApi.DetailNames(row));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
    }

    [Theory]
    [InlineData("sign_in")]
    [InlineData("connect")]
    [InlineData("expired")]
    [InlineData("used")]
    [InlineData("foreign")]
    [InlineData("another-accounts-login")]
    [InlineData("no-binding")]
    [InlineData("another-binding")]
    [InlineData("blank")]
    [InlineData("unknown")]
    public async Task A_ticket_that_is_not_a_live_reauth_ticket_of_this_account_is_403_reauth_failed_and_is_left_alone(string how)   // criterion 11
    {
        var (_, first) = await PasswordlessAccountAsync();
        var session = first;
        if (how == "another-accounts-login")
        {
            var other = await CreateUserAsync("someone@gmail.test", confirmed: true);
            await LinkAsync(other.Id, "google", "100000000000000000099");
        }

        var ticket = await TicketAsync(
            "google",
            how switch
            {
                "foreign" => GooglePerson(email: "someone@gmail.test", subject: "100000000000000000098"),
                "another-accounts-login" => GooglePerson(email: "someone@gmail.test", subject: "100000000000000000099"),
                _ => GooglePerson(),
            },
            how switch { "sign_in" => "", "connect" => "?purpose=connect", _ => "?purpose=reauth" });
        var value = how switch
        {
            "blank" => "",
            "unknown" => "not-a-ticket-of-anyone",
            _ => ticket.Value,
        };
        string? binding = ticket.Binding;
        switch (how)
        {
            case "expired":
                Clock.Advance(ProviderTicketsReauthLifetime() + TimeSpan.FromSeconds(1));
                session = await SessionApi.RefreshOk(Client, session.RefreshToken);   // the access token ran out meanwhile, the session did not
                break;
            case "used":
                using (var usedUp = await SettingsApi.ConfirmWithTicket(Client, session, ticket))
                {
                    Assert.Equal(HttpStatusCode.NoContent, usedUp.StatusCode);
                }

                await InDbAsync(db => db.SessionStates.ExecuteUpdateAsync(
                    s => s.SetProperty(state => state.ConfirmedUntil, (DateTimeOffset?)null), TestContext.Current.CancellationToken));
                break;
            case "no-binding":
                binding = null;
                break;
            case "another-binding":
                binding = (await TicketAsync("microsoft", PersonalMicrosoft())).Binding;
                break;
        }

        var tickets = await TicketCountAsync();
        var confirmed = (await AuditAsync(AuditKinds.AccountConfirmed)).Count;
        using var response = await SettingsApi.Confirm(Client, session, new { provider_ticket = value }, binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Null(ProviderApi.BindingCookieOf(response));   // auth_pt is not cleared: the browser keeps what it has
        Assert.Equal(tickets, await TicketCountAsync());   // nothing used up
        Assert.False(await WindowIsOpenAsync());
        Assert.Equal(confirmed, (await AuditAsync(AuditKinds.AccountConfirmed)).Count);
        var row = (await AuditAsync(AuditKinds.AccountConfirmRefused)).Single();
        Assert.Equal(("reauth_failed", "confirm"), (AuditApi.Text(row, "reason"), AuditApi.Text(row, "action")));
        Assert.Equal(0, await AttemptsAsync());
    }

    private static TimeSpan ProviderTicketsReauthLifetime() => Auth.Server.Providers.ProviderTickets.ReauthLifetime;

    [Theory]
    [InlineData("no-binding")]
    [InlineData("another-binding")]
    public async Task A_ticket_refused_for_its_binding_is_still_good_for_the_browser_that_holds_it(string how)   // criterion 11: rollback
    {
        var (_, session) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
        var wrong = how == "no-binding" ? null : (await TicketAsync("microsoft", PersonalMicrosoft())).Binding;

        using var refused = await SettingsApi.Confirm(Client, session, new { provider_ticket = ticket.Value }, wrong);
        using var accepted = await SettingsApi.ConfirmWithTicket(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    [Fact]
    public async Task The_confirmation_must_fit_the_account_before_anything_is_counted()   // criterion 11, Settled 10; check order: fit before lockout
    {
        var (_, withPassword) = await PasswordAccountAsync();
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
        await LockoutApi.FailAsync(Client, Clock, Ola, 10);
        var attempts = await AttemptsAsync();

        using var ticketForPassword = await SettingsApi.ConfirmWithTicket(Client, withPassword, reauth);
        await TenancyApi.AssertErrorAsync(ticketForPassword, HttpStatusCode.Forbidden, "password_required");   // not 429: the fit comes first

        var (_, passwordless) = await PrivateThroughProviderAsync("microsoft", PersonalMicrosoft());
        using var passwordForPasswordless = await SettingsApi.Confirm(Client, passwordless, new { password = "Any-Passw0rd-1" });
        await TenancyApi.AssertErrorAsync(passwordForPasswordless, HttpStatusCode.Forbidden, "reauth_required");

        Assert.Equal(attempts, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task A_password_for_an_account_without_one_is_reauth_required_before_the_lockout_is_consulted()   // check order: fit before counting, step 3 before 4
    {
        var (_, session) = await PasswordlessAccountAsync();
        await LockoutApi.FailAsync(Client, Clock, Ola, 10);   // the streak of this account is locked
        var attempts = await AttemptsAsync();
        Assert.True(attempts > 0);

        using var response = await SettingsApi.Confirm(Client, session, new { password = UserPassword });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_required");   // not 429: the fit comes first
        Assert.Equal(attempts, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
    }

    [Fact]
    public async Task A_ticket_neither_consults_nor_clears_the_lockout()   // criterion 11, spec 0012 → Limits
    {
        var (_, session) = await PasswordlessAccountAsync();
        await LockoutApi.FailAsync(Client, Clock, Ola, 10);   // a password login of an account without one counts as an unknown address
        var attempts = await AttemptsAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await SettingsApi.ConfirmWithTicket(Client, session, ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(attempts, await AttemptsAsync());
        Assert.True(attempts > 0);   // the streak is there and stays as it was
    }

    public static TheoryData<string> BadBodies => new()
    {
        "not json",
        "{}",
        """["password"]""",
        """{"password":"Another-Passw0rd","provider_ticket":"x"}""",
        """{"password":""}""",
        """{"password":"   "}""",
        """{"password":null}""",
        """{"password":5}""",
        """{"provider_ticket":5}""",
        """{"provider_ticket":null}""",
        "{\"password\":\"Another" + Backslash + "u0000Passw0rd\"}",   // NUL in the password
        "{\"provider_ticket\":\"" + Backslash + "ud800\"}",   // an unpaired surrogate
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request_before_the_session_and_counts_for_nothing(string body)   // criterion 11, step 1
    {
        var (_, session) = await PasswordAccountAsync();

        using var response = await SettingsApi.PostRaw(Client, SettingsApi.ConfirmPath, session, body);
        using var noCookie = await SettingsApi.PostRaw(Client, SettingsApi.ConfirmPath, session with { RefreshToken = "" }, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.BadRequest, "invalid_request");   // the body is judged first
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
    }

    [Fact]
    public async Task Without_the_refresh_cookie_it_is_401_session_required_and_without_a_token_the_plain_401()   // criterion 11
    {
        var (_, session) = await PasswordAccountAsync();

        using var noCookie = await SettingsApi.PostAs(Client, SettingsApi.ConfirmPath, session.AccessToken, null, new { password = UserPassword });
        using var noToken = await SettingsApi.PostAs(Client, SettingsApi.ConfirmPath, null, session.RefreshToken, new { password = UserPassword });

        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");
        await TenancyApi.AssertUnauthorizedAsync(noToken);
        Assert.Equal(0, await AttemptsAsync());   // the session comes before the password
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
    }

    [Theory]
    [InlineData("another-account")]
    [InlineData("garbage")]
    [InlineData("logged-out")]
    public async Task A_cookie_that_names_no_live_session_of_the_tokens_account_is_401_session_required_and_nothing_is_counted(string how)   // criterion 11
    {
        var (_, session) = await PasswordAccountAsync();
        var cookie = "not-a-refresh-token";
        switch (how)
        {
            case "another-account":
                await CreateUserAsync("other@firma.test", confirmed: true);
                cookie = (await SessionApi.LoginAsync(Client, "other@firma.test", UserPassword)).RefreshToken;
                break;
            case "logged-out":
                using (var logout = await SessionApi.Logout(Client, session.RefreshToken))
                {
                    Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
                }

                cookie = session.RefreshToken;
                break;
        }

        using var response = await SettingsApi.PostAs(Client, SettingsApi.ConfirmPath, session.AccessToken, cookie, new { password = UserPassword });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
    }

    [Fact]
    public async Task A_token_without_org_id_is_403_no_membership_and_nothing_is_written()   // spec 0012 B2
    {
        await CreateUserAsync(Ola, confirmed: true);
        var session = await _oldTokens.LoginWithoutAsync(Client, Ola, UserPassword, "org_id");
        Assert.False(AccessTokens.Payload(session.AccessToken).ContainsKey("org_id"), "The minted access token must carry no org_id.");
        Clock.Advance(TimeSpan.FromMinutes(6));   // the window of the sign-in is closed

        using var response = await SettingsApi.Confirm(Client, session, new { password = UserPassword });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.False(await WindowIsOpenAsync());
    }

    [Fact]
    public async Task A_token_for_a_company_the_account_left_is_403_no_membership_even_with_a_personal_space()   // spec 0012 B2: never the sign-in space
    {
        var (userId, session) = await PasswordAccountAsync();
        await AddPersonalSpaceAsync(userId);
        await RemoveMembershipAsync(userId, await DevCompanyIdAsync());

        using var response = await SettingsApi.Confirm(Client, session, new { password = UserPassword });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.False(await WindowIsOpenAsync());
    }

    [Fact]
    public async Task An_account_whose_membership_was_removed_is_403_no_membership_and_nothing_is_written()   // criterion 11
    {
        var (userId, session) = await PasswordAccountAsync();
        await RemoveMembershipAsync(userId);

        using var response = await SettingsApi.Confirm(Client, session, new { password = UserPassword });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.False(await WindowIsOpenAsync());
    }

    [Fact]
    public async Task The_session_and_the_membership_are_judged_before_the_fit()   // check order: step 2 before step 3
    {
        var (userId, session) = await PasswordlessAccountAsync();
        var wrongKind = new { password = "Any-Passw0rd-1" };   // an account without a password: the fit would say reauth_required
        using var noCookie = await SettingsApi.PostAs(Client, SettingsApi.ConfirmPath, session.AccessToken, null, wrongKind);
        await RemoveMembershipAsync(userId);

        using var noMembership = await SettingsApi.Confirm(Client, session, wrongKind);
        using var noCookieNoMembership = await SettingsApi.PostAs(Client, SettingsApi.ConfirmPath, session.AccessToken, null, wrongKind);

        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");
        await TenancyApi.AssertErrorAsync(noMembership, HttpStatusCode.Forbidden, "no_membership");
        await TenancyApi.AssertErrorAsync(noCookieNoMembership, HttpStatusCode.Unauthorized, "session_required");   // 401 before 403 inside step 2
    }

    [Fact]
    public async Task A_session_from_before_this_version_gets_its_values_written_by_the_confirmation()   // spec 0012 → Sessions
    {
        var (userId, session) = await PasswordAccountAsync();
        await InDbAsync(db => db.SessionStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        using var response = await SettingsApi.Confirm(Client, session, new { password = UserPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var subject = userId.ToString();
        var created = await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Where(a => a.Subject == subject)
            .Select(a => a.CreationDate).SingleAsync(TestContext.Current.CancellationToken));
        var row = await InDbAsync(db => db.SessionStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(created!.Value, DateTimeKind.Utc)), row.SessionStart);
        Assert.Equal(StorableTime.Now(Clock) + ConfirmationWindow.Lifetime, row.ConfirmedUntil);
        Assert.Equal(OpenUntil(), await SettingsApi.ConfirmedUntilAsync(Client, session));
    }

    [Fact]
    public async Task A_confirmation_ends_no_session_and_redeems_no_cookie()   // guard lesson 8: what must not happen
    {
        var (_, session) = await PasswordAccountAsync();
        _ = await SessionApi.LoginAsync(Client, Ola, UserPassword);   // another device of the account
        var before = await SessionRowsAsync();

        using var response = await SettingsApi.Confirm(Client, session, new { password = UserPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before, await SessionRowsAsync());   // every token and authorization as it was: none revoked, none redeemed
        using var refreshed = await SessionApi.Refresh(Client, session.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }

    private Task<string> SessionRowsAsync() =>
        InDbAsync(async db =>
        {
            var tokens = await db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().OrderBy(t => t.Id)
                .Select(t => t.Id + ":" + t.Status + ":" + t.RedemptionDate).ToListAsync(TestContext.Current.CancellationToken);
            var authorizations = await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking().OrderBy(a => a.Id)
                .Select(a => a.Id + ":" + a.Status).ToListAsync(TestContext.Current.CancellationToken);
            return string.Join('\n', tokens.Concat(authorizations));
        });

    /// <summary>Sends the confirmation while the address lock is held, waits until it is queued behind it, runs <paramref name="change"/>, releases.</summary>
    private async Task<HttpResponseMessage> ConfirmWhileLockedAsync(Func<Task<HttpResponseMessage>> confirm, Func<Task> change)
    {
        await using var held = await HeldLock.AddressAsync(Factory, OlaNormalized);
        var pending = confirm();
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await change();
        await held.ReleaseAsync();
        return await pending;
    }

    [Fact]
    public async Task A_ticket_used_up_while_the_confirmation_waited_for_the_lock_is_403_reauth_failed()   // guard lesson 1
    {
        var (_, session) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ConfirmWhileLockedAsync(
            () => SettingsApi.ConfirmWithTicket(Client, session, ticket),
            () => InDbAsync(db => db.ProviderTickets.ExecuteDeleteAsync(TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Equal("reauth_failed", AuditApi.Text((await AuditAsync(AuditKinds.AccountConfirmRefused)).Single(), "reason"));
    }

    [Fact]
    public async Task A_login_removed_while_the_confirmation_waited_is_403_reauth_failed_and_the_ticket_stays()   // guard lesson 1: the identity, again under the lock
    {
        var (_, session) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ConfirmWhileLockedAsync(
            () => SettingsApi.ConfirmWithTicket(Client, session, ticket),
            () => InDbAsync(db => db.UserLogins.ExecuteDeleteAsync(TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await TicketCountAsync());   // the rollback gave the ticket back
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
    }

    [Fact]
    public async Task Two_requests_with_one_ticket_exactly_one_confirms()   // guard lesson 1: one ticket, two requests
    {
        var (_, session) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        await using var held = await HeldLock.AddressAsync(Factory, OlaNormalized);
        var first = SettingsApi.ConfirmWithTicket(Client, session, ticket);
        var second = SettingsApi.ConfirmWithTicket(Client, session, ticket);
        await HeldLock.WaitForWaitersAsync(Factory, 2, first, second);
        await held.ReleaseAsync();
        using var a = await first;
        using var b = await second;

        Assert.Equal(new[] { HttpStatusCode.NoContent, HttpStatusCode.Forbidden }, new[] { a.StatusCode, b.StatusCode }.Order());
        await TenancyApi.AssertErrorAsync(a.StatusCode == HttpStatusCode.NoContent ? b : a, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(0, await TicketCountAsync());
        Assert.Single(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Single(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.Equal(OpenUntil(), await SettingsApi.ConfirmedUntilAsync(Client, session));
    }

    [Fact]
    public async Task A_ticket_that_expired_while_the_confirmation_waited_is_refused()   // guard lesson 11: the clock after the locks
    {
        var (_, session) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ConfirmWhileLockedAsync(
            () => SettingsApi.ConfirmWithTicket(Client, session, ticket),
            () =>
            {
                Clock.Advance(TimeSpan.FromMinutes(5));
                return Task.CompletedTask;
            });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
    }

    [Fact]
    public async Task The_window_runs_from_the_moment_the_lock_was_taken()   // guard lesson 11: the window is counted from the clock after the locks
    {
        var (_, session) = await PasswordAccountAsync();

        using var response = await ConfirmWhileLockedAsync(
            () => SettingsApi.Confirm(Client, session, new { password = UserPassword }),
            () =>
            {
                Clock.Advance(TimeSpan.FromMinutes(2));
                return Task.CompletedTask;
            });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(OpenUntil(), await SettingsApi.ConfirmedUntilAsync(Client, session));
    }

    /// <summary>The confirmation of a race test: a password for an account with one, or a reauth ticket for an account without.</summary>
    private async Task<(Guid UserId, SessionApi.Session Session, Func<Task<HttpResponseMessage>> Confirm)> ConfirmationAsync(bool withTicket)
    {
        if (!withTicket)
        {
            var (userId, session) = await PasswordAccountAsync();
            return (userId, session, () => SettingsApi.Confirm(Client, session, new { password = UserPassword }));
        }

        var (id, passwordless) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
        return (id, passwordless, () => SettingsApi.ConfirmWithTicket(Client, passwordless, ticket));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_membership_removed_while_the_confirmation_waited_is_403_no_membership(bool withTicket)   // spec 0012 → General rules: under the lock
    {
        var (userId, _, confirm) = await ConfirmationAsync(withTicket);

        using var response = await ConfirmWhileLockedAsync(confirm, () => RemoveMembershipAsync(userId));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Equal(withTicket ? 1 : 0, await TicketCountAsync());   // a ticket is not used up
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.False(await WindowIsOpenAsync());
    }

    [Fact]
    public async Task A_password_set_while_a_provider_confirmation_waited_is_403_password_required_and_the_ticket_stays()   // spec 0012 → General rules: the fit, again under the lock
    {
        var (userId, session) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ConfirmWhileLockedAsync(
            () => SettingsApi.ConfirmWithTicket(Client, session, ticket),
            () => InDbAsync(db => db.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, "set-meanwhile"), TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "password_required");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
    }

    [Fact]
    public async Task A_password_removed_while_a_password_confirmation_waited_is_403_reauth_required_and_the_window_stays_closed()   // the fit, again under the lock, the other way
    {
        var (userId, session) = await PasswordAccountAsync();

        using var response = await ConfirmWhileLockedAsync(
            () => SettingsApi.Confirm(Client, session, new { password = UserPassword }),
            () => InDbAsync(db => db.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_required");
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.False(await WindowIsOpenAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_session_ended_while_the_confirmation_waited_is_401_session_required_and_opens_nothing(bool withTicket)   // under the lock
    {
        var (_, session, confirm) = await ConfirmationAsync(withTicket);

        using var response = await ConfirmWhileLockedAsync(
            confirm,
            async () =>
            {
                using var logout = await SessionApi.Logout(Client, session.RefreshToken);
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Equal(withTicket ? 1 : 0, await TicketCountAsync());   // a ticket is not used up
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.False(await WindowIsOpenAsync());
    }

    [Fact]
    public async Task No_password_ticket_binding_or_cookie_reaches_the_audit_log_or_the_log()   // guard lesson 4
    {
        var (_, session) = await PasswordAccountAsync();
        List<string> bodies = [];
        using (var wrong = await SettingsApi.Confirm(Client, session, new { password = "Wrong-Passw0rd-9" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            bodies.Add(await wrong.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var right = await SettingsApi.Confirm(Client, session, new { password = UserPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
            bodies.Add(await right.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var (_, passwordless) = await PrivateThroughProviderAsync("microsoft", PersonalMicrosoft());
        var refusedTicket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");   // an identity that is no login of this account
        using (var refused = await SettingsApi.ConfirmWithTicket(Client, passwordless, refusedTicket))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            bodies.Add(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var ticket = await TicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");
        using (var confirmed = await SettingsApi.ConfirmWithTicket(Client, passwordless, ticket))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
            bodies.Add(await confirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        string[] secrets =
        [
            "Wrong-Passw0rd-9", UserPassword, ticket.Value, ticket.Binding, refusedTicket.Value, refusedTicket.Binding,
            session.RefreshToken, passwordless.RefreshToken, session.AccessToken,
        ];
        await AssertNoSecretsAsync(secrets);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal));
        Assert.All(secrets, secret => Assert.All(bodies, body => Assert.DoesNotContain(secret, body, StringComparison.Ordinal)));
    }
}
