using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012 → Sessions and The confirmation window (criteria 8 and 9): every sign-in that starts a session opens a window of five minutes on
/// it; a refresh keeps it; it is bound to the session of the refresh cookie, and <c>GET /auth/me</c> says when it ends.
/// </summary>
public sealed class ConfirmationWindowTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;

    private string OpenUntil() => SettingsApi.Window(StorableTime.Now(Clock) + ConfirmationWindow.Lifetime);

    /// <summary>A session started by one of the sign-ins of criterion 8, each with an account of its own.</summary>
    private async Task<SessionApi.Session> SignInAsync(string how)
    {
        switch (how)
        {
            case "login":
                await CreateUserAsync("login@firma.test", confirmed: true);
                return await SessionApi.LoginAsync(Client, "login@firma.test", UserPassword);
            case "exchange":
            {
                var user = await CreateUserAsync(Ola, confirmed: true);
                await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
                using var exchanged = await ProviderApi.Exchange(Client, await TicketAsync("google", GooglePerson()));
                return await SessionApi.ReadSessionAsync(exchanged);
            }
            case "link":
            {
                await CreateUserAsync(Ola, confirmed: true);
                using var linked = await ProviderApi.Link(Client, await TicketAsync("google", GooglePerson()), UserPassword);
                return await SessionApi.ReadSessionAsync(linked);
            }
            case "finish":
                return (await PrivateThroughProviderAsync("google", GooglePerson(email: "finish@gmail.test", subject: "700000000000000000007"))).Session;
            case "invitation":
            {
                var company = await CreateCompanyAsync("Acme");
                var invite = await AddInviteAsync(company, "jan@gmail.test", "user");
                await EnqueueInvitationAsync(invite, "jan@gmail.test");
                await DispatchAsync();
                var token = TokenIn(Mail.Sent.Last(m => m.To == "jan@gmail.test"));
                var ticket = await TicketAsync("google", GooglePerson(email: "jan@gmail.test", subject: "900000000000000000009"), "?invite=" + token);
                using var accepted = await ProviderApi.Exchange(Client, ticket);
                return await SessionApi.ReadSessionAsync(accepted);
            }
            default:
            {
                var token = await MailedPrivateAsync("complete@firma.test");
                using var completed = await SignupApi.CompletePrivate(Client, token, Password);
                return await SessionApi.ReadSessionAsync(completed);
            }
        }
    }

    [Theory]
    [InlineData("login")]
    [InlineData("exchange")]
    [InlineData("link")]
    [InlineData("finish")]
    [InlineData("invitation")]
    [InlineData("completion")]
    public async Task Every_sign_in_that_starts_a_session_opens_its_window_for_five_minutes(string how)   // criterion 8
    {
        var session = await SignInAsync(how);

        Assert.Equal(OpenUntil(), await SettingsApi.ConfirmedUntilAsync(Client, session));
        Assert.Equal(1, await InDbAsync(db => db.SessionStates.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_password_acceptance_of_an_invitation_opens_no_window()   // criterion 8: the 204 of spec 0005 signs nobody in
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "anna@acme.test", "user");
        await EnqueueInvitationAsync(invite, "anna@acme.test");
        await DispatchAsync();
        var states = await InDbAsync(db => db.SessionStates.CountAsync(TestContext.Current.CancellationToken));

        using var accepted = await TenancyApi.Accept(Client, TokenIn(Mail.Sent.Last(m => m.To == "anna@acme.test")), Password);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(states, await InDbAsync(db => db.SessionStates.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task At_five_minutes_and_one_second_the_window_is_closed()   // criterion 8: a controlled clock
    {
        var session = await SignInAsync("login");
        var until = OpenUntil();

        Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.Equal(until, await SettingsApi.ConfirmedUntilAsync(Client, session));

        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
    }

    [Fact]
    public async Task A_refresh_keeps_the_window_and_never_extends_it()   // criterion 8
    {
        var session = await SignInAsync("login");
        var until = OpenUntil();

        Clock.Advance(TimeSpan.FromMinutes(3));
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Assert.Equal(until, await SettingsApi.ConfirmedUntilAsync(Client, refreshed));

        Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        var again = await SessionApi.RefreshOk(Client, refreshed.RefreshToken);
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, again));
    }

    [Fact]
    public async Task Two_sessions_of_one_account_have_two_windows()   // criterion 9: bound to the session, not to the account
    {
        var first = await SignInAsync("login");
        Clock.Advance(TimeSpan.FromMinutes(4));
        var second = await SessionApi.LoginAsync(Client, "login@firma.test", UserPassword);
        Clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, first));
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, second));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, second.AccessToken, first.RefreshToken));   // the cookie decides, not the token
    }

    [Fact]
    public async Task A_sign_out_closes_the_window_of_its_session()   // criterion 9
    {
        var session = await SignInAsync("login");

        using (var logout = await SessionApi.Logout(Client, session.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));   // the access token still works for its ten minutes
    }

    [Fact]
    public async Task After_a_reset_or_an_account_deletion_the_old_session_names_nothing()   // criterion 9: EndAllAsync, an account deletion
    {
        var session = await SignInAsync("login");
        using (var forgot = await AccountApi.Forgot(Client, "login@firma.test"))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        await DispatchAsync();
        using (var reset = await AccountApi.Reset(Client, TokenIn(Mail.Sent.Last(m => m.To == "login@firma.test")), "Chosen-Passw0rd-2"))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));

        await CreatePrivateAccountAsync("alone@poczta.test");
        var privateSession = await SessionApi.LoginAsync(Client, "alone@poczta.test", UserPassword);
        var resetAuthorizationId = (await ReadAsync(await TokenIdAsync(session.RefreshToken), AccountOf(session)))?.AuthorizationId;
        Assert.Null(resetAuthorizationId);   // the reset ended that session: its token and authorization are revoked
        var deletedTokenId = await TokenIdAsync(privateSession.RefreshToken);
        var deletedAuthorizationId = (await ReadAsync(deletedTokenId, AccountOf(privateSession)))!.AuthorizationId;
        var before = await InDbAsync(db => db.SessionStates.Select(s => s.AuthorizationId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Contains(deletedAuthorizationId, before);
        using (var deleted = await ProviderApi.DeleteAccount(Client, privateSession.AccessToken, new { password = UserPassword }, null))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var left = await InDbAsync(db => db.SessionStates.Select(s => s.AuthorizationId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(deletedAuthorizationId, left);   // the deleted account's session went, with its row
        Assert.Equal(before.Where(id => id != deletedAuthorizationId).Order(StringComparer.Ordinal), left.Order(StringComparer.Ordinal));   // and nothing else
        Assert.Null(await ReadAsync(deletedTokenId, AccountOf(privateSession)));
    }

    [Fact]
    public async Task Without_the_cookie_with_another_accounts_a_revoked_one_or_garbage_me_says_null()   // criterion 9
    {
        var mine = await SignInAsync("login");
        await CreateUserAsync("other@firma.test", confirmed: true);
        var theirs = await SessionApi.LoginAsync(Client, "other@firma.test", UserPassword);

        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, mine));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, mine.AccessToken, null));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, mine.AccessToken, theirs.RefreshToken));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, mine.AccessToken, "not-a-token-at-all"));
        using (var logout = await SessionApi.Logout(Client, theirs.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, theirs));
    }

    [Fact]
    public async Task A_refresh_cookie_redeemed_within_the_leeway_still_names_the_session()   // criterion 9: 15 seconds
    {
        var session = await SignInAsync("login");
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        Clock.Advance(TimeSpan.FromSeconds(14));
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, refreshed.AccessToken, session.RefreshToken));

        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, refreshed.AccessToken, session.RefreshToken));
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, refreshed));
    }

    [Fact]
    public async Task A_session_without_a_row_has_a_closed_window()   // spec 0012 → Sessions: a session that began before this version
    {
        var session = await SignInAsync("login");
        await InDbAsync(db => db.SessionStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
        Assert.Equal(1, await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>().CountAsync(TestContext.Current.CancellationToken)));
    }

    // ---- The session of this device, read by DeviceSessions (spec 0012 → General rules; Amendment A8) ----

    private static Guid AccountOf(SessionApi.Session session) => Guid.Parse(AccessTokens.Text(session.AccessToken, "sub"));

    /// <summary>The id of the refresh token the cookie names, found through the token manager as the endpoints find it.</summary>
    private async Task<string> TokenIdAsync(string cookie)
    {
        using var scope = Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var token = await tokens.FindByReferenceIdAsync(cookie, TestContext.Current.CancellationToken);
        return await tokens.GetIdAsync(token!, TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("The token has no id.");
    }

    /// <summary>A re-read by the token's id, in a scope of its own, as a request under a lock does it.</summary>
    private async Task<DeviceSession?> ReadAsync(string tokenId, Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        return await DeviceSessions.ReadAsync(
            scope.ServiceProvider.GetRequiredService<AuthDbContext>(), tokenId, userId, StorableTime.Now(Clock), TestContext.Current.CancellationToken);
    }

    private static DateTime UtcOf(DateTimeOffset value) => DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Utc);

    private Task<int> TamperTokenAsync(
        string tokenId, Action<UpdateSettersBuilder<OpenIddictEntityFrameworkCoreToken>> change) =>
        InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreToken>().Where(t => t.Id == tokenId).ExecuteUpdateAsync(change, TestContext.Current.CancellationToken));

    [Fact]
    public async Task The_session_names_its_token_its_authorization_its_account_its_stored_start_and_its_window()
    {
        var session = await SignInAsync("login");
        var started = StorableTime.Now(Clock);
        var tokenId = await TokenIdAsync(session.RefreshToken);
        var stored = await InDbAsync(db => db.SessionStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));

        Clock.Advance(TimeSpan.FromMinutes(1));
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
        var refreshedId = await TokenIdAsync(refreshed.RefreshToken);
        var found = await ReadAsync(refreshedId, AccountOf(session));

        Assert.NotNull(found);
        Assert.Equal(refreshedId, found.TokenId);
        Assert.NotEqual(tokenId, found.TokenId);   // a refresh makes a new token of the same session
        Assert.Equal(stored.AuthorizationId, found.AuthorizationId);
        Assert.Equal(AccountOf(session), found.UserId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(started.ToUnixTimeSeconds()), found.Start);   // the start is kept to the second, as the session_start claim is
        Assert.Equal(started + ConfirmationWindow.Lifetime, found.ConfirmedUntil);
        Assert.True(found.IsConfirmedAt(started + TimeSpan.FromMinutes(4)));
        Assert.False(found.IsConfirmedAt(started + ConfirmationWindow.Lifetime));   // the end itself is closed
    }

    [Fact]
    public async Task A_session_from_before_this_version_began_when_its_authorization_was_made_and_has_a_closed_window()
    {
        var session = await SignInAsync("login");
        var created = await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Select(a => a.CreationDate).SingleAsync(TestContext.Current.CancellationToken));
        await InDbAsync(db => db.SessionStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        Clock.Advance(TimeSpan.FromHours(1));

        var found = await ReadAsync(await TokenIdAsync(session.RefreshToken), AccountOf(session));

        Assert.NotNull(found);
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(created!.Value, DateTimeKind.Utc)), found.Start);
        Assert.Null(found.ConfirmedUntil);
        Assert.False(found.IsConfirmedAt(StorableTime.Now(Clock)));
    }

    [Fact]
    public async Task An_authorization_without_a_creation_date_and_without_a_row_names_no_session()
    {
        var session = await SignInAsync("login");
        var tokenId = await TokenIdAsync(session.RefreshToken);
        await InDbAsync(db => db.SessionStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(await ReadAsync(tokenId, AccountOf(session)));   // with a creation date it is a session of before this version

        await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
            .ExecuteUpdateAsync(a => a.SetProperty(x => x.CreationDate, (DateTime?)null), TestContext.Current.CancellationToken));

        Assert.Null(await ReadAsync(tokenId, AccountOf(session)));
    }

    [Fact]
    public async Task The_reread_sees_what_other_requests_committed_meanwhile()
    {
        var session = await SignInAsync("login");
        var tokenId = await TokenIdAsync(session.RefreshToken);
        var userId = AccountOf(session);
        Assert.True((await ReadAsync(tokenId, userId))!.IsConfirmedAt(StorableTime.Now(Clock)));

        await InDbAsync(db => db.SessionStates.ExecuteUpdateAsync(s => s.SetProperty(x => x.ConfirmedUntil, (DateTimeOffset?)null), TestContext.Current.CancellationToken));
        var closed = await ReadAsync(tokenId, userId);
        Assert.NotNull(closed);
        Assert.Null(closed.ConfirmedUntil);

        await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
            .ExecuteUpdateAsync(a => a.SetProperty(x => x.Status, OpenIddictConstants.Statuses.Revoked), TestContext.Current.CancellationToken));
        Assert.Null(await ReadAsync(tokenId, userId));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("token_revoked")]
    [InlineData("authorization_revoked")]
    [InlineData("redeemed_without_a_date")]
    [InlineData("redeemed_at_the_end_of_the_leeway")]
    [InlineData("redeemed_past_the_leeway")]
    [InlineData("another_account")]
    [InlineData("an_access_token_entry")]
    public async Task A_token_that_is_not_a_live_refresh_token_of_a_valid_authorization_of_this_account_names_nothing(string defect)   // Amendment A8
    {
        var session = await SignInAsync("login");
        var tokenId = await TokenIdAsync(session.RefreshToken);
        var userId = AccountOf(session);
        Assert.NotNull(await ReadAsync(tokenId, userId));   // the token is good until one thing is wrong with it
        var now = UtcOf(StorableTime.Now(Clock));
        var toRead = tokenId;
        var reader = userId;

        switch (defect)
        {
            case "expired":
                await TamperTokenAsync(tokenId, t => t.SetProperty(x => x.ExpirationDate, (DateTime?)now.AddSeconds(-1)));
                break;
            case "token_revoked":
                await TamperTokenAsync(tokenId, t => t.SetProperty(x => x.Status, OpenIddictConstants.Statuses.Revoked));
                break;
            case "authorization_revoked":
                await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
                    .ExecuteUpdateAsync(a => a.SetProperty(x => x.Status, OpenIddictConstants.Statuses.Revoked), TestContext.Current.CancellationToken));
                break;
            case "redeemed_without_a_date":
                await TamperTokenAsync(tokenId, t => t
                    .SetProperty(x => x.Status, OpenIddictConstants.Statuses.Redeemed)
                    .SetProperty(x => x.RedemptionDate, (DateTime?)null));
                break;
            case "redeemed_at_the_end_of_the_leeway":
                await TamperTokenAsync(tokenId, t => t
                    .SetProperty(x => x.Status, OpenIddictConstants.Statuses.Redeemed)
                    .SetProperty(x => x.RedemptionDate, (DateTime?)now.Subtract(SessionPolicy.ReuseLeeway)));
                break;
            case "redeemed_past_the_leeway":
                await TamperTokenAsync(tokenId, t => t
                    .SetProperty(x => x.Status, OpenIddictConstants.Statuses.Redeemed)
                    .SetProperty(x => x.RedemptionDate, (DateTime?)now.Subtract(SessionPolicy.ReuseLeeway).AddSeconds(-1)));
                break;
            case "another_account":
                reader = Guid.NewGuid();
                break;
            default:
                // The access token of the session has an entry of its own, of the same authorization and account: not a refresh token.
                toRead = (await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreToken>()
                    .Where(t => t.Type == OpenIddictConstants.TokenTypeIdentifiers.AccessToken)
                    .Select(t => t.Id)
                    .SingleAsync(TestContext.Current.CancellationToken)))!;
                break;
        }

        Assert.Null(await ReadAsync(toRead, reader));
    }

    [Fact]
    public async Task A_token_redeemed_a_moment_ago_still_names_the_session_until_the_leeway_ends()
    {
        var session = await SignInAsync("login");
        var tokenId = await TokenIdAsync(session.RefreshToken);
        var now = UtcOf(StorableTime.Now(Clock));
        await TamperTokenAsync(tokenId, t => t
            .SetProperty(x => x.Status, OpenIddictConstants.Statuses.Redeemed)
            .SetProperty(x => x.RedemptionDate, (DateTime?)now.Subtract(SessionPolicy.ReuseLeeway).AddSeconds(1)));

        Assert.NotNull(await ReadAsync(tokenId, AccountOf(session)));
    }

    [Fact]
    public async Task A_cookie_far_longer_than_a_token_names_nothing()
    {
        var session = await SignInAsync("login");

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session.AccessToken, new string('a', RefreshCookie.MaxTokenLength + 1)));
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, session));
    }

    [Fact]
    public async Task Reading_the_session_of_this_device_never_redeems_its_cookie_and_leaves_no_secret_in_the_log()
    {
        var session = await SignInAsync("login");

        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, session));
        }

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session.AccessToken, "garbage-cookie-value-of-this-test"));
        var tokenId = await TokenIdAsync(session.RefreshToken);
        var token = await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().SingleAsync(t => t.Id == tokenId, TestContext.Current.CancellationToken));
        Assert.Equal(OpenIddictConstants.Statuses.Valid, token.Status);
        Assert.Null(token.RedemptionDate);
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);   // the cookie is still good for its refresh

        foreach (var secret in new[] { session.RefreshToken, session.AccessToken, refreshed.RefreshToken, "garbage-cookie-value-of-this-test" })
        {
            Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_confirmation_in_one_session_leaves_another_session_unconfirmed()   // criterion 9, Review Focus 5
    {
        var here = await SignInAsync("login");
        var there = await SessionApi.LoginAsync(Client, "login@firma.test", UserPassword);
        Clock.Advance(TimeSpan.FromMinutes(6));

        using var confirmed = await SettingsApi.Confirm(Client, here, new { password = UserPassword });

        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.Equal(OpenUntil(), await SettingsApi.ConfirmedUntilAsync(Client, here));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, there));
    }

    [Fact]
    public async Task At_five_minutes_and_one_second_the_connect_is_403_confirmation_required_and_changes_nothing()   // criterion 8
    {
        var session = await SignInAsync("login");
        var ticket = await TicketAsync("google", GooglePerson(email: "login@firma.test", subject: "100000000000000000066"), "?purpose=connect");
        Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        var until = await SettingsApi.ConfirmedUntilAsync(Client, session);
        Assert.NotNull(until);   // the second before the end: the window still covers the connect

        Clock.Advance(TimeSpan.FromSeconds(2));
        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "confirmation_required");
        Assert.Equal(0, await InDbAsync(db => db.UserLogins.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));   // the refusal did not touch the window either
    }

    [Fact]
    public async Task At_five_minutes_and_one_second_the_unlink_is_403_confirmation_required_and_changes_nothing()   // criterion 8
    {
        var session = await SignInAsync("login");
        var userId = await InDbAsync(db => db.Users.Where(u => u.NormalizedEmail == "LOGIN@FIRMA.TEST").Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken));
        await LinkAsync(userId, "google", "100000000000000000066");
        Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        using var response = await SettingsApi.Unlink(Client, session, "google");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "confirmation_required");
        Assert.Equal(1, await InDbAsync(db => db.UserLogins.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task After_a_renewal_the_old_session_names_nothing()   // criterion 9
    {
        var session = await SignInAsync("login");
        var userId = await InDbAsync(db => db.Users.Where(u => u.NormalizedEmail == "LOGIN@FIRMA.TEST").Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken));
        await LinkAsync(userId, "google", "100000000000000000066");

        using var response = await SettingsApi.Unlink(Client, session, "google");
        var renewed = await SessionApi.ReadSessionAsync(response);

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, renewed.AccessToken, session.RefreshToken));
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, renewed));
    }
}
