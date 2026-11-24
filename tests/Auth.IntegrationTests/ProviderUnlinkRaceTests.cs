using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012 → Sign-in of a linked identity, and the link (criterion 15): a sign-in of a linked identity that races the unlink of its login either
/// ends first, and the unlink then ends its session, or answers <c>400 invalid_ticket</c>; no session of the identity outlives the unlink. Both
/// orders are forced: the sign-in is stopped before its account read (it holds the login row by then) or before its login read.
/// </summary>
public sealed class ProviderUnlinkRaceTests : ProviderTestBase
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string OlaNormalized = "OLA@GMAIL.TEST";

    // The login read of a sign-in is the only statement of these flows with this text ("FOR KEY SHARE" does not contain it).
    private const string LoginHold = "FOR SHARE";
    private const string AccountHold = "FOR KEY SHARE";

    private readonly ShareLockInterceptor _race = new();

    public ProviderUnlinkRaceTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(_race)));
    }

    /// <summary>An account with a password and a Google login; this device signed in with the password a moment ago (its window open).</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> AccountAsync()
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        return (user.Id, await SessionApi.LoginAsync(Client, Ola, UserPassword));
    }

    private Task<int> LiveAuthorizationsAsync() =>
        InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
            .CountAsync(a => a.Status == OpenIddictConstants.Statuses.Valid, TestContext.Current.CancellationToken));

    private Task<List<string>> LoginsOfAsync(Guid userId) =>
        InDbAsync(db => db.UserLogins.Where(l => l.UserId == userId).Select(l => l.LoginProvider).ToListAsync(TestContext.Current.CancellationToken));

    /// <summary>
    /// Stops the next request before the command with <paramref name="pattern"/> (after <paramref name="skip"/> others like it), sends
    /// <paramref name="step"/> then, and lets the request go on only once the step's request waits for a lock. The accessor returns the step's answer.
    /// </summary>
    private Func<Task<HttpResponseMessage>> StopAndSend(string pattern, int skip, Func<Task<HttpResponseMessage>> step)
    {
        Task<HttpResponseMessage>? sent = null;
        _race.Pattern = pattern;
        _race.Skip = skip;
        _race.Before = async () =>
        {
            sent = step();
            await HeldLock.WaitForWaitersAsync(Factory, 1, sent);
        };
        return () => sent ?? throw new InvalidOperationException("The request never reached the command it was to be stopped before.");
    }

    /// <summary>
    /// Stops the next sign-in at the account read of its commit (the second one: the hold has read the account's row first), when it holds the
    /// account's row and its login row, and sends the unlink then.
    /// </summary>
    private Func<Task<HttpResponseMessage>> UnlinkWhileTheSignInHoldsTheLogin(SessionApi.Session session) =>
        StopAndSend(AccountHold, 1, () => SettingsApi.Unlink(Client, session, "google"));

    /// <summary>Stops the next sign-in before it takes its membership row (the first lock of its order) and unlinks the login then: the unlink commits first.</summary>
    private void UnlinkBeforeTheSignInReadsTheLogin(SessionApi.Session session)
    {
        _race.Pattern = "FOR NO KEY UPDATE";
        _race.Before = async () =>
        {
            using var unlinked = await SettingsApi.Unlink(Client, session, "google");   // commits before the sign-in reads the login again
            Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        };
    }

    /// <summary>
    /// Sends the link while the address lock is held, waits until it is queued behind it, runs <paramref name="change"/>, releases: the link then
    /// finds the identity linked under its lock and signs in.
    /// </summary>
    private async Task<HttpResponseMessage> LinkWhileLockedAsync(ProviderApi.Ticket ticket, Func<Task> change)
    {
        await using var held = await HeldLock.AddressAsync(Factory, OlaNormalized);
        var link = ProviderApi.Link(Client, ticket, UserPassword);
        await HeldLock.WaitForWaitersAsync(Factory, 1, link);
        await change();
        await held.ReleaseAsync();
        return await link;
    }

    [Fact]
    public async Task An_exchange_that_holds_the_login_first_ends_first_and_the_unlink_then_ends_its_session()   // criterion 15
    {
        var (userId, session) = await AccountAsync();
        var ticket = await TicketAsync("google", GooglePerson());
        var unlink = UnlinkWhileTheSignInHoldsTheLogin(session);

        using var exchanged = await ProviderApi.Exchange(Client, ticket);

        var signedIn = await SessionApi.ReadSessionAsync(exchanged);
        Assert.True(_race.Fired);
        using var unlinked = await unlink();
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, signedIn.RefreshToken));   // its session ended with the others
        Assert.Equal(1, await LiveAuthorizationsAsync());   // the renewed session of the unlinking device, and nothing else
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Equal(0, await TicketCountAsync());   // the sign-in used its ticket up once
    }

    [Fact]
    public async Task An_exchange_that_holds_the_login_and_a_deletion_of_the_account_neither_deadlock()   // lock order: the account's row before the login rows
    {
        var (userId, _) = await CreatePrivateAccountAsync(Ola);   // an account with a space of its own can delete itself
        await LinkAsync(userId, "google", FakePeople.GoogleSubject);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var ticket = await TicketAsync("google", GooglePerson());

        // Stopped before the login read: the sign-in holds the account's row and nothing else. The deletion must queue behind that row; with the
        // login row read first the deletion would finish at once, and then the sign-in would wait for the account's row while the deletion
        // waited for the login row.
        var deletion = StopAndSend(LoginHold, 0, () => ProviderApi.DeleteAccount(Client, session.AccessToken, new { password = UserPassword }, null));

        using var exchanged = await ProviderApi.Exchange(Client, ticket);

        Assert.True(_race.Fired);
        var signedIn = await SessionApi.ReadSessionAsync(exchanged);   // the sign-in ended first: the deletion waited for its share lock
        using var deleted = await deletion();
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == userId, TestContext.Current.CancellationToken)));
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, signedIn.RefreshToken));
        Assert.Equal(0, await LiveAuthorizationsAsync());
    }

    [Fact]
    public async Task An_unlink_that_holds_its_revocations_and_a_member_removal_of_its_account_neither_deadlock()   // lock order: the account's row before the login rows
    {
        var (company, _, adminToken) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Ola, "user");
        await LinkAsync(worker, "google", FakePeople.GoogleSubject);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        // Stopped at its read of the account's row at the commit, when it has deleted the login and revoked the sessions: the removal (no address
        // lock) must queue behind the unlink's share lock on that row. An unlink that took the row only now would wait for a removal that waits
        // for the login and the sessions.
        var removal = StopAndSend(
            AccountHold, 1, () => TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", adminToken));

        using var unlinked = await SettingsApi.Unlink(Client, session, "google");

        Assert.True(_race.Fired);
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        using var removed = await removal();
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await LiveAuthorizationsAsync());   // the admin's session only
    }

    [Fact]
    public async Task An_exchange_after_an_unlink_that_committed_first_is_400_invalid_ticket()   // criterion 15
    {
        var (userId, session) = await AccountAsync();
        var ticket = await TicketAsync("google", GooglePerson());
        UnlinkBeforeTheSignInReadsTheLogin(session);

        using var exchanged = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(exchanged, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.True(_race.Fired);
        Assert.False(exchanged.Headers.Contains("Set-Cookie"));   // auth_pt is not cleared, no refresh cookie is set
        Assert.Equal(1, await TicketCountAsync());   // the rollback gave the ticket back
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.DoesNotContain(await AuditAsync(AuditKinds.LoginSucceeded), row => AuditApi.Text(row, "provider") == "google");
        Assert.Equal(1, await LiveAuthorizationsAsync());   // only the renewed session of the unlinking device
    }

    [Fact]
    public async Task A_link_whose_identity_is_a_login_signs_in_first_and_the_unlink_then_ends_its_session()   // criterion 15: the link's step 3
    {
        var (userId, session) = await AccountAsync();
        var ticket = await TicketAsync("google", GooglePerson());
        var unlink = UnlinkWhileTheSignInHoldsTheLogin(session);

        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);   // the identity is a login: step 3 signs in without the password

        var signedIn = await SessionApi.ReadSessionAsync(linked);
        Assert.True(_race.Fired);
        using var unlinked = await unlink();
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, signedIn.RefreshToken));
        Assert.Equal(1, await LiveAuthorizationsAsync());
        Assert.Empty(await LoginsOfAsync(userId));
    }

    [Fact]
    public async Task A_link_after_an_unlink_that_committed_first_is_400_invalid_ticket()   // criterion 15: the link's step 3
    {
        var (userId, session) = await AccountAsync();
        var ticket = await TicketAsync("google", GooglePerson());
        UnlinkBeforeTheSignInReadsTheLogin(session);

        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);

        await TenancyApi.AssertErrorAsync(linked, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.True(_race.Fired);
        Assert.False(linked.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, await TicketCountAsync());   // the rollback gave the ticket back
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.DoesNotContain(await AuditAsync(AuditKinds.LoginSucceeded), row => AuditApi.Text(row, "provider") == "google");
        Assert.Equal(1, await LiveAuthorizationsAsync());
    }

    [Fact]
    public async Task A_link_that_finds_the_identity_linked_under_its_lock_signs_in_first_and_the_unlink_then_ends_its_session()   // the link's "linked meanwhile" branch
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var ticket = await TicketAsync("google", GooglePerson());
        var unlink = UnlinkWhileTheSignInHoldsTheLogin(session);

        using var linked = await LinkWhileLockedAsync(ticket, () => LinkAsync(user.Id, "google", FakePeople.GoogleSubject));

        var signedIn = await SessionApi.ReadSessionAsync(linked);
        Assert.True(_race.Fired);
        using var unlinked = await unlink();
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, signedIn.RefreshToken));
        Assert.Equal(1, await LiveAuthorizationsAsync());
        Assert.Empty(await LoginsOfAsync(user.Id));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));   // the link did not link: the identity was a login by then
    }

    [Fact]
    public async Task A_link_that_finds_the_identity_linked_under_its_lock_is_400_invalid_ticket_when_an_unlink_commits_before_its_login_read()   // the link's "linked meanwhile" branch
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var ticket = await TicketAsync("google", GooglePerson());
        UnlinkBeforeTheSignInReadsTheLogin(session);

        using var linked = await LinkWhileLockedAsync(ticket, () => LinkAsync(user.Id, "google", FakePeople.GoogleSubject));

        await TenancyApi.AssertErrorAsync(linked, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.True(_race.Fired);
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(user.Id));
        Assert.DoesNotContain(await AuditAsync(AuditKinds.LoginSucceeded), row => AuditApi.Text(row, "provider") == "google");
        Assert.Equal(1, await LiveAuthorizationsAsync());
    }
}
