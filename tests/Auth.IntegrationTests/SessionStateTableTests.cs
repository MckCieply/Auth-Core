using System.Globalization;
using System.Net;
using System.Security.Claims;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → Sessions (Amendment A8): the table <c>SessionStates</c> as the migration makes it, and the row every new session writes.</summary>
public sealed class SessionStateTableTests : TenancyTestBase
{
    private const string Ola = "ola@poczta.test";

    public SessionStateTableTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The pruning loops wait on the fake clock: a test that moves it by two weeks drives the pass by hand.
        Factory.WithoutHostedService<TokenPruningService>().WithoutHostedService<LockoutPruningService>();
    }

    private sealed record SessionRow(string Id, string? Status, string? Type, DateTimeOffset? Start, DateTimeOffset? Until);

    private Task<List<string>> StringsAsync(FormattableString sql) =>
        InDbAsync(db => db.Database.SqlQuery<string>(sql).ToListAsync(TestContext.Current.CancellationToken));

    /// <summary>The authorizations of the account, oldest first, each with its row of <c>SessionStates</c> (null values without one).</summary>
    private async Task<List<SessionRow>> SessionsOfAsync(Guid userId)
    {
        var subject = userId.ToString();
        var rows = await InDbAsync(db => (
            from authorization in db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking()
            where authorization.Subject == subject
            orderby authorization.CreationDate
            select new
            {
                authorization.Id,
                authorization.Status,
                authorization.Type,
                Start = db.SessionStates.Where(s => s.AuthorizationId == authorization.Id).Select(s => (DateTimeOffset?)s.SessionStart).FirstOrDefault(),
                Until = db.SessionStates.Where(s => s.AuthorizationId == authorization.Id).Select(s => s.ConfirmedUntil).FirstOrDefault(),
            }).ToListAsync(TestContext.Current.CancellationToken));
        return [.. rows.Select(r => new SessionRow(r.Id!, r.Status, r.Type, r.Start, r.Until))];
    }

    private Task<int> RowCountAsync() => InDbAsync(db => db.SessionStates.CountAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task The_migration_makes_the_table()   // Amendment A8
    {
        var columns = await StringsAsync(
            $"""
            SELECT column_name || ':' || data_type || ':' || is_nullable AS "Value"
            FROM information_schema.columns WHERE table_name = 'SessionStates' ORDER BY column_name
            """);

        Assert.Equal(
            ["AuthorizationId:text:NO", "ConfirmedUntil:timestamp with time zone:YES", "SessionStart:timestamp with time zone:NO"],
            columns);
    }

    [Fact]
    public async Task The_row_names_its_authorization_and_goes_with_it()   // spec 0012 → Sessions: the values cannot outlive their session
    {
        var keys = await StringsAsync(
            $"""
            SELECT confdeltype::text || ':' || confrelid::regclass::text AS "Value"
            FROM pg_constraint WHERE conrelid = '"SessionStates"'::regclass AND contype = 'f'
            """);
        var primary = await StringsAsync($"""SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'SessionStates' ORDER BY indexname""");

        Assert.Equal(["c:\"OpenIddictAuthorizations\""], keys);   // ON DELETE CASCADE
        Assert.Equal(["PK_SessionStates"], primary);
    }

    [Fact]
    public async Task The_mail_requests_have_a_nullable_provider_of_twenty_characters()   // spec 0012 → Mails, the queue
    {
        var column = await StringsAsync(
            $"""
            SELECT data_type || ':' || character_maximum_length::text || ':' || is_nullable AS "Value"
            FROM information_schema.columns WHERE table_name = 'MailRequests' AND column_name = 'Provider'
            """);

        Assert.Equal(["character varying:20:YES"], column);
    }

    [Fact]
    public async Task A_login_makes_the_sessions_authorization_with_a_row_of_its_start_and_a_window_of_five_minutes()   // criterion 8
    {
        var now = StorableTime.Now(Clock);

        await SessionApi.LoginAsync(Client, Factory);

        var session = Assert.Single(await SessionsOfAsync(await Factory.SeedUserIdAsync()));
        Assert.Equal(OpenIddictConstants.Statuses.Valid, session.Status);
        Assert.Equal(OpenIddictConstants.AuthorizationTypes.AdHoc, session.Type);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()), session.Start);   // the refresh token's session_start
        Assert.Equal(now + TimeSpan.FromMinutes(5), session.Until);
        Assert.Equal(TimeSpan.FromMinutes(5), ConfirmationWindow.Lifetime);
    }

    [Fact]
    public async Task A_refresh_keeps_the_authorization_its_start_and_its_window_and_never_extends_it()   // criterion 8
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        var before = Assert.Single(await SessionsOfAsync(await Factory.SeedUserIdAsync()));

        Clock.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await SessionApi.RefreshOk(Client, login.RefreshToken);
        Clock.Advance(TimeSpan.FromMinutes(2));
        await SessionApi.RefreshOk(Client, refreshed.RefreshToken);

        Assert.Equal(before, Assert.Single(await SessionsOfAsync(await Factory.SeedUserIdAsync())));
    }

    [Fact]
    public async Task Two_logins_are_two_sessions_with_two_windows()   // criterion 9: bound to the session, not to the account
    {
        await SessionApi.LoginAsync(Client, Factory);
        Clock.Advance(TimeSpan.FromMinutes(3));
        await SessionApi.LoginAsync(Client, Factory);

        var sessions = await SessionsOfAsync(await Factory.SeedUserIdAsync());

        Assert.Equal(2, sessions.Count);
        Assert.Equal(TimeSpan.FromMinutes(3), sessions[1].Until - sessions[0].Until);
    }

    [Fact]
    public async Task A_logout_revokes_the_authorization_and_the_pruner_removes_it_with_its_row()   // spec 0012 → Sessions
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        using (var logout = await SessionApi.Logout(Client, login.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        Assert.Equal(OpenIddictConstants.Statuses.Revoked, Assert.Single(await SessionsOfAsync(await Factory.SeedUserIdAsync())).Status);
        Assert.Equal(1, await RowCountAsync());

        Clock.Advance(TimeSpan.FromDays(15));
        await Factory.Services.GetRequiredService<TokenPruner>().PruneOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await SessionsOfAsync(await Factory.SeedUserIdAsync()));
        Assert.Equal(0, await RowCountAsync());
    }

    [Fact]
    public async Task A_deleted_account_takes_its_sessions_and_their_rows_with_it()   // spec 0012 → Sessions: an account deletion deletes it
    {
        var (userId, _) = await CreatePrivateAccountAsync(Ola);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        Assert.Equal(1, await RowCountAsync());

        using var deleted = await TenancyApi.Send(Client, HttpMethod.Delete, "/auth/account", session.AccessToken, new { password = UserPassword });

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await SessionsOfAsync(userId));
        Assert.Equal(0, await RowCountAsync());
    }

    [Fact]
    public async Task A_wrong_password_makes_no_authorization_and_no_row()   // the row is written only with a session
    {
        using var login = await LoginApi.Login(Client, Factory.SeedEmail, "Wrong-Passw0rd-1");

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Empty(await SessionsOfAsync(await Factory.SeedUserIdAsync()));
        Assert.Equal(0, await RowCountAsync());
    }

    [Fact]
    public async Task A_login_whose_audit_row_cannot_be_written_leaves_no_authorization_and_no_row()   // the authorization and its row go with the transaction
    {
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var login = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

        Assert.Equal(HttpStatusCode.InternalServerError, login.StatusCode);
        Assert.False(login.Headers.Contains("Set-Cookie"), "A login that failed must not write the cookie.");
        Assert.Empty(await SessionsOfAsync(await Factory.SeedUserIdAsync()));
        Assert.Equal(0, await RowCountAsync());
    }

    private static ClaimsPrincipal PrincipalOf(HttpContext http, FakeTimeProvider clock, DateTimeOffset? start)
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), SecurityStamp = "stamp" };
        var tenant = new TenantContext(user.Id, Guid.NewGuid(), "Acme", Guid.NewGuid(), "admin", ["org:manage"], false, CompanyKind.Organization, DateTimeOffset.UnixEpoch);
        return SessionPrincipal.Create(http, user, tenant, "https://api.test", clock, start);
    }

    [Fact]
    public void A_renewal_keeps_the_start_of_its_session_and_lives_what_is_left_before_the_cap()   // spec 0012 → Sessions: the cap counts from the old start
    {
        var now = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var http = new DefaultHttpContext();
        var start = now - TimeSpan.FromDays(20);

        var principal = PrincipalOf(http, clock, start);

        Assert.Equal(start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), principal.GetClaim(SessionPolicy.StartClaim));
        Assert.Equal(TimeSpan.FromDays(10), principal.GetRefreshTokenLifetime());
        Assert.Equal(TimeSpan.FromDays(10), http.Items[RefreshCookie.LifetimeItemKey]);
    }

    [Fact]
    public void A_new_session_starts_now_and_lives_the_sliding_lifetime_and_one_past_its_cap_cannot_be_renewed()   // spec 0012 → Sessions
    {
        var now = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var http = new DefaultHttpContext();

        var principal = PrincipalOf(http, clock, null);

        Assert.Equal(now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), principal.GetClaim(SessionPolicy.StartClaim));
        Assert.Equal(SessionPolicy.SlidingLifetime, principal.GetRefreshTokenLifetime());
        Assert.Throws<InvalidOperationException>(() => PrincipalOf(new DefaultHttpContext(), clock, now - TimeSpan.FromDays(30)));
    }

    [Fact]
    public void A_window_is_written_in_utc_to_the_second()   // spec 0012 → GET /auth/me: ISO 8601 with seconds
    {
        var instant = new DateTimeOffset(2026, 5, 4, 20, 35, 7, 123, TimeSpan.FromHours(2));

        Assert.Equal("2026-05-04T18:35:07Z", ConfirmationWindow.Format(instant));
        Assert.Equal(new SessionValues(instant, instant + TimeSpan.FromMinutes(5)), SessionValues.Opened(instant, instant));
    }
}
