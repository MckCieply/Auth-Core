using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → <c>POST /auth/account/logins/unlink</c> (criteria 16 and 17), in the order of its checks.</summary>
public sealed class ProviderUnlinkTests : ProviderTestBase
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string OlaNormalized = "OLA@GMAIL.TEST";

    private readonly ShareLockInterceptor _race = new();
    private readonly SessionRowFault _fault = new();

    public ProviderUnlinkTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(_race, _fault)));
    }

    private Task<List<string>> ProvidersOfAsync(Guid userId) =>
        InDbAsync(db => db.UserLogins.AsNoTracking().Where(l => l.UserId == userId).Select(l => l.LoginProvider).Order()
            .ToListAsync(TestContext.Current.CancellationToken));

    /// <summary>The values of the one session whose authorization is still valid.</summary>
    private Task<SessionState> LiveSessionStateAsync() =>
        InDbAsync(db => (
            from state in db.SessionStates.AsNoTracking()
            join authorization in db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking() on state.AuthorizationId equals authorization.Id
            where authorization.Status == OpenIddictConstants.Statuses.Valid
            select state).SingleAsync(TestContext.Current.CancellationToken));

    /// <summary>Every authorization, every token and every mail limit of the database, with the state each one is in.</summary>
    private Task<(string[] Authorizations, string[] Tokens, string[] Limits)> SessionRowsAsync() =>
        InDbAsync(async db => (
            await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking().OrderBy(a => a.Id).Select(a => a.Id + ":" + a.Status)
                .ToArrayAsync(TestContext.Current.CancellationToken),
            await db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id + ":" + t.Status)
                .ToArrayAsync(TestContext.Current.CancellationToken),
            await db.MailRequestLimits.AsNoTracking().OrderBy(l => l.Kind).Select(l => l.Kind + ":" + l.WindowCount)
                .ToArrayAsync(TestContext.Current.CancellationToken)));

    private Task<int> UnlinkMailsAsync() =>
        InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.ProviderUnlinked, TestContext.Current.CancellationToken));

    /// <summary>An account with a password and a Google login, signed in on this device a moment ago (its window open).</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> PasswordAccountAsync()
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        return (user.Id, await SessionApi.LoginAsync(Client, Ola, UserPassword));
    }

    /// <summary>A private account made with Google, then given a Microsoft login, signed in by the finish (its window open).</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> PasswordlessWithTwoAsync()
    {
        var made = await PrivateThroughProviderAsync("google", GooglePerson());
        await LinkAsync(made.UserId, "microsoft", FakePeople.PersonalTenant + ":" + FakePeople.PersonalObjectId);
        return made;
    }

    /// <summary>Sends the unlink and holds it behind the address lock until <paramref name="meanwhile"/> has run, then lets it go.</summary>
    private async Task<HttpResponseMessage> UnlinkAfterAsync(SessionApi.Session session, string provider, Func<Task> meanwhile)
    {
        Task<HttpResponseMessage> pending;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            pending = SettingsApi.Unlink(Client, session, provider);
            await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
            await meanwhile();
            await held.ReleaseAsync();
        }

        return await pending;
    }

    [Fact]
    public async Task An_unlink_with_an_open_window_deletes_the_login_signs_the_other_devices_out_and_renews_this_one()   // criterion 16, Review Focus 1
    {
        var (userId, here) = await PasswordAccountAsync();
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        await LockoutApi.FailAsync(Client, Clock, Ola, 2);
        using (var forgot = await AccountApi.Forgot(Client, Ola))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        await DispatchAsync();   // an email link of the account exists now
        var before = await InDbAsync(db => db.SessionStates.AsNoTracking().OrderBy(s => s.SessionStart).FirstAsync(TestContext.Current.CancellationToken));
        var window = await SettingsApi.ConfirmedUntilAsync(Client, here);
        var rowsBefore = (await AuditAsync()).Count;
        Clock.Advance(TimeSpan.FromMinutes(2));   // the renewed window must be the old one, not five minutes from now

        using var response = await SettingsApi.Unlink(Client, here, "google");

        var rowsAfter = await AuditAsync();
        Assert.Equal([AuditKinds.LoginSucceeded, AuditKinds.ProviderUnlinked], rowsAfter.Skip(rowsBefore).Select(r => r.Kind).Order(StringComparer.Ordinal));   // nothing else is written
        var renewed = await SessionApi.ReadSessionAsync(response);
        Assert.Equal(["access_token", "status"], await JsonNamesAsync(response));
        Assert.Equal(userId.ToString(), AccessTokens.Text(renewed.AccessToken, "sub"));
        Assert.Empty(await ProvidersOfAsync(userId));
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, there.RefreshToken));
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, here.RefreshToken));
        Assert.Equal(window, await SettingsApi.ConfirmedUntilAsync(Client, renewed));   // the old window, unchanged
        var live = await LiveSessionStateAsync();   // exactly one session is alive
        Assert.Equal((before.SessionStart, before.ConfirmedUntil), (live.SessionStart, live.ConfirmedUntil));

        var unlinked = await SingleAsync(AuditKinds.ProviderUnlinked);
        Assert.Equal((userId, userId, Ola), (unlinked.ActorUserId!.Value, unlinked.SubjectUserId!.Value, unlinked.SubjectEmail));
        Assert.Equal(await DevCompanyIdAsync(), unlinked.OrgId);
        Assert.Equal(["provider"], AuditApi.DetailNames(unlinked));
        Assert.Equal("google", AuditApi.Text(unlinked, "provider"));
        var renewal = (await AuditAsync(AuditKinds.LoginSucceeded))[^1];
        Assert.Equal(["via"], AuditApi.DetailNames(renewal));
        Assert.Equal("session_renewal", AuditApi.Text(renewal, "via"));
        Assert.Equal(1, await UnlinkMailsAsync());
        Assert.Equal(1, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == userId, TestContext.Current.CancellationToken)));   // left alone
        Assert.Equal(1, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));   // left alone

        // The new session works, and the Google identity no longer signs in to the account.
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, renewed.RefreshToken)).StatusCode);
        using var exchanged = await ProviderApi.Exchange(Client, await TicketAsync("google", GooglePerson()));
        Assert.Equal("link", (await TenancyApi.ReadOkAsync(exchanged)).GetProperty("next").GetString());
    }

    private static async Task<string[]> JsonNamesAsync(HttpResponseMessage response)
    {
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task A_renewed_session_keeps_the_cap_of_its_start_and_one_past_its_cap_is_401_session_required()   // criterion 17, Review Focus 1
    {
        var (userId, session) = await PasswordAccountAsync();
        Clock.Advance(TimeSpan.FromDays(13));
        session = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Clock.Advance(TimeSpan.FromDays(13));
        session = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using (var confirmed = await SettingsApi.Confirm(Client, session, new { password = UserPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        using var response = await SettingsApi.Unlink(Client, session, "google");

        var renewed = await SessionApi.ReadSessionAsync(response);
        Assert.InRange(renewed.Cookie.MaxAge!.Value, TimeSpan.FromDays(4) - TimeSpan.FromSeconds(2), TimeSpan.FromDays(4));   // 30 days from the start

        // One past its cap: the stored start is more than 30 days ago.
        await LinkAsync(userId, "google", FakePeople.GoogleSubject);
        await InDbAsync(db => db.SessionStates.ExecuteUpdateAsync(
            s => s.SetProperty(state => state.SessionStart, StorableTime.Now(Clock) - TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken));
        var rows = (await AuditAsync()).Count;
        var mails = await UnlinkMailsAsync();

        using var past = await SettingsApi.Unlink(Client, renewed, "google");

        await TenancyApi.AssertErrorAsync(past, HttpStatusCode.Unauthorized, "session_required");
        Assert.False(past.Headers.Contains("Set-Cookie"));
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Equal(rows, (await AuditAsync()).Count);
        Assert.Equal(mails, await UnlinkMailsAsync());
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, renewed.RefreshToken)).StatusCode);   // nothing was revoked
    }

    [Fact]
    public async Task A_session_that_crosses_its_cap_while_the_unlink_waited_is_401_session_required_and_nothing_changes()   // the cap, read under the lock
    {
        var (userId, here) = await PasswordAccountAsync();
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var rows = (await AuditAsync()).Count;

        using var response = await UnlinkAfterAsync(here, "google", () => InDbAsync(db => db.SessionStates.ExecuteUpdateAsync(
            s => s.SetProperty(state => state.SessionStart, StorableTime.Now(Clock) - TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Equal(rows, (await AuditAsync()).Count);
        Assert.Equal(0, await UnlinkMailsAsync());
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, there.RefreshToken)).StatusCode);   // no session was revoked
    }

    [Fact]
    public async Task A_failure_after_the_sessions_ended_rolls_the_whole_unlink_back()   // guard lesson 9: the sign-in's transaction owns the change
    {
        var (userId, here) = await PasswordAccountAsync();
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        await LockoutApi.FailAsync(Client, Clock, Ola, 2);
        var rows = (await AuditAsync()).Count;
        var states = await InDbAsync(db => db.SessionStates.CountAsync(TestContext.Current.CancellationToken));
        var rowsBefore = await SessionRowsAsync();
        _fault.Armed = true;

        using var failed = await SettingsApi.Unlink(Client, here, "google");

        Assert.True(_fault.Fired, "The renewed session never reached its write.");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.False(failed.Headers.Contains("Set-Cookie"));   // the refresh cookie is not replaced
        Assert.Equal(["google"], await ProvidersOfAsync(userId));   // the login row is still there
        Assert.Equal(rows, (await AuditAsync()).Count);   // neither provider.unlinked nor login.succeeded
        Assert.Equal(0, await UnlinkMailsAsync());
        Assert.Equal(states, await InDbAsync(db => db.SessionStates.CountAsync(TestContext.Current.CancellationToken)));
        var rowsAfter = await SessionRowsAsync();
        Assert.Equal(rowsBefore.Authorizations, rowsAfter.Authorizations);   // none created, none revoked
        Assert.Equal(rowsBefore.Tokens, rowsAfter.Tokens);   // none created, none revoked
        Assert.Equal(rowsBefore.Limits, rowsAfter.Limits);   // the notice limit was not counted
        Assert.Equal(1, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, there.RefreshToken)).StatusCode);   // no session was revoked
        _fault.Armed = false;
        here = await SessionApi.RefreshOk(Client, here.RefreshToken);

        using var again = await SettingsApi.Unlink(Client, here, "google");   // nothing was used up: the same request, now that nothing fails

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Empty(await ProvidersOfAsync(userId));
    }

    [Fact]
    public async Task A_closed_window_is_403_confirmation_required_and_changes_nothing()   // criterion 17
    {
        var (userId, session) = await PasswordAccountAsync();
        Clock.Advance(TimeSpan.FromMinutes(6));
        var rows = (await AuditAsync()).Count;

        using var response = await SettingsApi.Unlink(Client, session, "google");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "confirmation_required");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Equal(rows, (await AuditAsync()).Count);
        Assert.Equal(0, await UnlinkMailsAsync());
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, session.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task A_window_that_ended_while_the_unlink_waited_is_403_confirmation_required_and_changes_nothing()   // the clock is read after the lock
    {
        var (userId, here) = await PasswordAccountAsync();
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var rows = (await AuditAsync()).Count;

        using var response = await UnlinkAfterAsync(here, "google", () =>
        {
            Clock.Advance(TimeSpan.FromMinutes(6));
            return Task.CompletedTask;
        });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "confirmation_required");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Equal(rows, (await AuditAsync()).Count);
        Assert.Equal(0, await UnlinkMailsAsync());
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, there.RefreshToken)).StatusCode);
    }

    [Theory]
    [InlineData("gitlab")]
    [InlineData("GOOGLE")]
    [InlineData("Google")]
    [InlineData(" google")]
    [InlineData("google ")]
    [InlineData("")]
    [InlineData("github")]   // a provider name the account has no login of
    [InlineData("microsoft")]
    public async Task A_name_that_is_no_provider_or_no_login_of_the_account_is_404_not_found(string provider)   // criterion 17
    {
        var (userId, session) = await PasswordAccountAsync();
        var rows = (await AuditAsync()).Count;

        using var response = await SettingsApi.Unlink(Client, session, provider);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Equal(rows, (await AuditAsync()).Count);
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, session.RefreshToken)).StatusCode);   // the session was not ended
    }

    [Fact]
    public async Task A_login_removed_while_the_unlink_waited_is_404_not_found()   // step 4 again under the lock
    {
        var (userId, session) = await PasswordAccountAsync();
        var rows = (await AuditAsync()).Count;

        using var response = await UnlinkAfterAsync(session, "google", () =>
            InDbAsync(db => db.UserLogins.Where(l => l.UserId == userId).ExecuteDeleteAsync(TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
        Assert.Equal(rows, (await AuditAsync()).Count);
        Assert.Equal(0, await UnlinkMailsAsync());
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, session.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task The_only_login_of_an_account_without_a_password_is_409_last_login()   // criterion 17
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        var rows = (await AuditAsync()).Count;

        using var response = await SettingsApi.Unlink(Client, session, "google");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "last_login");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderUnlinked));
        Assert.Equal(rows, (await AuditAsync()).Count);
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, session.RefreshToken)).StatusCode);   // the session was not ended
    }

    [Fact]
    public async Task The_last_offered_login_is_409_last_login_and_a_login_no_longer_offered_can_go_while_a_way_in_remains()   // criterion 17
    {
        var (userId, session) = await PasswordlessWithTwoAsync();
        SetManifest(AuthAppFactory.SelfServiceManifest + AuthAppFactory.PrivateBlock + "\nproviders: [google]\n", withProviders: false);   // Microsoft is no longer offered

        using (var last = await SettingsApi.Unlink(Client, session, "google"))
        {
            await TenancyApi.AssertErrorAsync(last, HttpStatusCode.Conflict, "last_login");
        }

        using var gone = await SettingsApi.Unlink(Client, session, "microsoft");

        Assert.Equal(HttpStatusCode.OK, gone.StatusCode);
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
    }

    [Fact]
    public async Task A_login_no_longer_offered_is_no_way_in_for_the_other_one()   // criterion 17: only an offered login counts
    {
        var (userId, session) = await PasswordlessWithTwoAsync();
        SetManifest(AuthAppFactory.SelfServiceManifest + AuthAppFactory.PrivateBlock + "\nproviders: [microsoft]\n", withProviders: false);   // Google is no longer offered

        using var response = await SettingsApi.Unlink(Client, session, "microsoft");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "last_login");   // Google remains, but it is not a way in
        Assert.Equal(["google", "microsoft"], await ProvidersOfAsync(userId));
    }

    [Fact]
    public async Task The_last_other_way_in_removed_while_the_unlink_waited_makes_it_409_last_login()   // step 5 again under the lock
    {
        var (userId, session) = await PasswordlessWithTwoAsync();
        var rows = (await AuditAsync()).Count;

        using var response = await UnlinkAfterAsync(session, "google", () =>
            InDbAsync(db => db.UserLogins.Where(l => l.UserId == userId && l.LoginProvider == "microsoft")
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "last_login");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Equal(rows, (await AuditAsync()).Count);
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, session.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Two_logins_of_one_provider_from_before_this_version_both_go()   // criterion 17
    {
        var (userId, session) = await PasswordAccountAsync();
        await LinkAsync(userId, "google", "100000000000000000077");

        using var response = await SettingsApi.Unlink(Client, session, "google");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ProvidersOfAsync(userId));
        Assert.Single(await AuditAsync(AuditKinds.ProviderUnlinked));
    }

    [Fact]
    public async Task Only_the_logins_of_the_named_provider_go()   // criterion 17
    {
        var (userId, session) = await PasswordlessWithTwoAsync();
        await LinkAsync(userId, "google", "100000000000000000077");

        using var response = await SettingsApi.Unlink(Client, session, "google");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["microsoft"], await ProvidersOfAsync(userId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_unlinks_of_the_two_logins_at_once_leave_one_login(bool twoSessions)   // criterion 17, with parallel requests
    {
        var (userId, here) = await PasswordlessWithTwoAsync();
        var there = here;
        if (twoSessions)
        {
            using var signedIn = await ProviderApi.Exchange(Client, await TicketAsync("microsoft", PersonalMicrosoft(email: Ola)));
            there = await SessionApi.ReadSessionAsync(signedIn);
        }

        Task<HttpResponseMessage>[] pending;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            pending = [SettingsApi.Unlink(Client, here, "google"), SettingsApi.Unlink(Client, there, "microsoft")];
            await HeldLock.WaitForWaitersAsync(Factory, 2, pending);
            await held.ReleaseAsync();
        }

        var answers = await Task.WhenAll(pending);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Unauthorized], answers.Select(a => a.StatusCode).Order());
        await TenancyApi.AssertErrorAsync(answers.Single(a => a.StatusCode == HttpStatusCode.Unauthorized), HttpStatusCode.Unauthorized, "session_required");
        Assert.Single(await ProvidersOfAsync(userId));
        Assert.Single(await AuditAsync(AuditKinds.ProviderUnlinked));
        Assert.Equal(1, await UnlinkMailsAsync());
        foreach (var answer in answers)
        {
            answer.Dispose();
        }
    }

    [Fact]
    public async Task A_session_ended_while_the_unlink_waited_is_401_session_required_and_nothing_changes()   // step 2 again under the lock
    {
        var (userId, session) = await PasswordAccountAsync();
        var rows = (await AuditAsync()).Count;

        using var response = await UnlinkAfterAsync(session, "google", () =>
            InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, OpenIddictConstants.Statuses.Revoked), TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Equal(rows, (await AuditAsync()).Count);
        Assert.Equal(0, await UnlinkMailsAsync());
    }

    [Fact]
    public async Task Without_the_refresh_cookie_it_is_401_session_required_and_without_a_token_the_plain_401()   // criterion 17
    {
        var (userId, session) = await PasswordAccountAsync();

        using var noCookie = await SettingsApi.PostAs(Client, SettingsApi.UnlinkPath, session.AccessToken, null, new { provider = "google" });
        using var noToken = await SettingsApi.PostAs(Client, SettingsApi.UnlinkPath, null, session.RefreshToken, new { provider = "google" });

        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");
        await TenancyApi.AssertUnauthorizedAsync(noToken);
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, session.RefreshToken)).StatusCode);   // the session was not ended
    }

    [Fact]
    public async Task A_removed_membership_is_403_no_membership_and_nothing_is_written()   // criterion 17
    {
        var (userId, session) = await PasswordAccountAsync();
        await RemoveMembershipAsync(userId);
        var rows = (await AuditAsync()).Count;

        using var response = await SettingsApi.Unlink(Client, session, "google");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderUnlinked));
        Assert.Equal(rows, (await AuditAsync()).Count);
        Assert.Equal(0, await UnlinkMailsAsync());
    }

    [Fact]
    public async Task A_membership_removed_while_the_unlink_waited_is_403_no_membership()   // under the lock
    {
        var (userId, session) = await PasswordAccountAsync();
        var rows = (await AuditAsync()).Count;

        using var response = await UnlinkAfterAsync(session, "google", () => RemoveMembershipAsync(userId));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Equal(rows, (await AuditAsync()).Count);
        Assert.Equal(0, await UnlinkMailsAsync());
    }

    [Fact]
    public async Task A_password_removed_while_the_unlink_waited_makes_it_409_last_login()   // step 5 again under the lock
    {
        var (userId, session) = await PasswordAccountAsync();

        using var response = await UnlinkAfterAsync(session, "google", () =>
            InDbAsync(db => db.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "last_login");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Equal(0, await UnlinkMailsAsync());
    }

    [Fact]
    public async Task A_reset_that_runs_while_the_unlink_holds_its_revocations_waits_for_it_and_neither_deadlocks()   // lock order: address, the account's row (share), logins, tokens, the account's row again
    {
        var (userId, here) = await PasswordAccountAsync();
        using (var forgot = await AccountApi.Forgot(Client, Ola))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        await DispatchAsync();
        var token = TokenIn(Mail.Sent.Last(m => m.To == Ola));
        Task<HttpResponseMessage>? reset = null;
        _race.Skip = 2;   // the first row lock is the unlink's membership row, the second the account's row right after it; this is its third, the read of the account's row at the commit
        _race.Before = async () =>
        {
            // The unlink holds the account's row for share since the address lock and has deleted the login and revoked the sessions: the reset
            // updates that row, then waits for the revoked rows. A lock on the row that conflicts with the share lock would be a cycle.
            reset = AccountApi.Reset(Client, token, "Brand-New-Passw0rd");
            await HeldLock.WaitForWaitersAsync(Factory, 1, reset);
        };

        using var unlinked = await SettingsApi.Unlink(Client, here, "google");

        Assert.True(_race.Fired);
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        using var resetAnswer = await reset!;
        Assert.Equal(HttpStatusCode.NoContent, resetAnswer.StatusCode);
        Assert.Empty(await ProvidersOfAsync(userId));
        var renewed = await SessionApi.ReadSessionAsync(unlinked);
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, renewed.RefreshToken));   // the reset changed the stamp the new session was built with
    }

    [Fact]
    public async Task The_checks_run_in_the_order_of_the_contract()   // guard lesson 10
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using (var unknown = await SettingsApi.Unlink(Client, session, "gitlab"))
        {
            await TenancyApi.AssertErrorAsync(unknown, HttpStatusCode.NotFound, "not_found");   // 4 before 5
        }

        Clock.Advance(TimeSpan.FromMinutes(6));
        session = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using var closed = await SettingsApi.Unlink(Client, session, "gitlab");
        await TenancyApi.AssertErrorAsync(closed, HttpStatusCode.Forbidden, "confirmation_required");   // 3 before 4
    }

    [Fact]
    public async Task The_session_and_the_membership_are_judged_before_the_window()   // 2 before 3
    {
        var (userId, session) = await PasswordAccountAsync();
        Clock.Advance(TimeSpan.FromMinutes(6));

        using (var noCookie = await SettingsApi.PostAs(Client, SettingsApi.UnlinkPath, session.AccessToken, null, new { provider = "google" }))
        {
            await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");
        }

        await RemoveMembershipAsync(userId);
        using var noMembership = await SettingsApi.Unlink(Client, session, "google");
        using var noCookieNoMembership = await SettingsApi.PostAs(Client, SettingsApi.UnlinkPath, session.AccessToken, null, new { provider = "google" });
        await TenancyApi.AssertErrorAsync(noMembership, HttpStatusCode.Forbidden, "no_membership");
        await TenancyApi.AssertErrorAsync(noCookieNoMembership, HttpStatusCode.Unauthorized, "session_required");   // the session is judged before the membership
    }

    [Fact]
    public async Task A_last_login_is_judged_before_the_cap_of_the_session()   // 5 before the cap
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        await InDbAsync(db => db.SessionStates.ExecuteUpdateAsync(
            s => s.SetProperty(state => state.SessionStart, StorableTime.Now(Clock) - TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken));

        using var response = await SettingsApi.Unlink(Client, session, "google");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "last_login");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
    }

    public static TheoryData<string> BadBodies => new() { "not json", "{}", """["provider"]""", """{"provider":5}""", """{"provider":null}""" };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request_before_the_session(string body)   // criterion 17, step 1
    {
        var (_, session) = await PasswordAccountAsync();

        using var response = await SettingsApi.PostRaw(Client, SettingsApi.UnlinkPath, session with { RefreshToken = "" }, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task No_token_or_cookie_reaches_the_audit_log_or_the_log()   // guard lesson 4
    {
        var (_, session) = await PasswordAccountAsync();

        using var response = await SettingsApi.Unlink(Client, session, "google");
        var renewed = await SessionApi.ReadSessionAsync(response);

        string[] secrets = [session.RefreshToken, session.AccessToken, renewed.RefreshToken, renewed.AccessToken];
        await AssertNoSecretsAsync(secrets);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal));
    }
}
