using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → <c>POST /auth/providers/connect</c> (criteria 12 and 13), in the order of its checks.</summary>
public sealed class ProviderConnectTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string OlaNormalized = "OLA@GMAIL.TEST";
    private const string OtherGoogleSubject = "100000000000000000077";
    private const string Kim = "kim@firma.test";

    /// <summary>Everything a connect could write, as the database holds it now: a refusal under the lock must leave all of it as it was.</summary>
    private sealed record Snapshot(int Logins, int LinkedRows, int Mails, int Tickets, string Sessions);

    private Task<List<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>> LoginsOfAsync(Guid userId) =>
        InDbAsync(db => db.UserLogins.AsNoTracking().Where(l => l.UserId == userId).ToListAsync(TestContext.Current.CancellationToken));

    private Task<int> NoticeCountAsync() =>
        InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.ProviderLinked, TestContext.Current.CancellationToken));

    private async Task<Snapshot> SnapshotAsync()
    {
        var states = await InDbAsync(db => db.SessionStates.AsNoTracking().OrderBy(s => s.AuthorizationId).ToListAsync(TestContext.Current.CancellationToken));
        return new Snapshot(
            await InDbAsync(db => db.UserLogins.CountAsync(TestContext.Current.CancellationToken)),
            (await AuditAsync(AuditKinds.ProviderLinked)).Count,
            await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)),
            await TicketCountAsync(),
            string.Join(';', states.Select(s => $"{s.AuthorizationId}|{s.SessionStart:O}|{s.ConfirmedUntil:O}")));
    }

    /// <summary>An account with a password, signed in on this device a moment ago: its window is open.</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> AccountAsync()
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        return (user.Id, await SessionApi.LoginAsync(Client, Ola, UserPassword));
    }

    private Task<ProviderApi.Ticket> ConnectTicketAsync(FakeSignIn? person = null, string provider = "google") =>
        TicketAsync(provider, person ?? GooglePerson(), "?purpose=connect");

    /// <summary>Sends the connect while a test holds the lock of the account's address, lets <paramref name="meanwhile"/> change the world, then releases.</summary>
    private async Task<HttpResponseMessage> ConnectWhileLockedAsync(SessionApi.Session session, ProviderApi.Ticket ticket, Func<Task> meanwhile)
    {
        await using var held = await HeldLock.AddressAsync(Factory, OlaNormalized);
        var pending = SettingsApi.Connect(Client, session, ticket);
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await meanwhile();
        await held.ReleaseAsync();
        return await pending;
    }

    [Fact]
    public async Task An_open_window_and_a_ticket_of_the_accounts_verified_address_adds_the_login_and_signs_nobody_out()   // criterion 12
    {
        var (userId, here) = await AccountAsync();
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var ticket = await ConnectTicketAsync();
        var window = await SettingsApi.ConfirmedUntilAsync(Client, here);

        using var response = await SettingsApi.Connect(Client, here, ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(response.Headers.CacheControl?.NoStore);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal(0, await TicketCountAsync());
        var login = Assert.Single(await LoginsOfAsync(userId));
        Assert.Equal(("google", FakePeople.GoogleSubject), (login.LoginProvider, login.ProviderKey));
        var row = await SingleAsync(AuditKinds.ProviderLinked);
        Assert.Equal((userId, userId, Ola), (row.ActorUserId!.Value, row.SubjectUserId!.Value, row.SubjectEmail));
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal(["provider", "via"], AuditApi.DetailNames(row));
        Assert.Equal(("google", "settings"), (AuditApi.Text(row, "provider"), AuditApi.Text(row, "via")));
        Assert.Equal(1, await NoticeCountAsync());
        Assert.Equal(2, (await AuditAsync(AuditKinds.LoginSucceeded)).Count);   // the two logins: the connect issued no session

        // Every session lives on, the window is as it was, and the provider alone signs in from now on.
        Assert.Equal(window, await SettingsApi.ConfirmedUntilAsync(Client, here));
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, here.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, there.RefreshToken)).StatusCode);
        using var exchanged = await ProviderApi.Exchange(Client, await TicketAsync("google", GooglePerson()));
        Assert.Equal(userId.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(exchanged)).AccessToken, "sub"));
    }

    [Theory]
    [InlineData("sign_in")]
    [InlineData("reauth")]
    [InlineData("unknown")]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("no-binding")]
    [InlineData("another-binding")]
    [InlineData("blank")]
    public async Task A_ticket_that_is_not_a_live_connect_ticket_of_this_browser_is_400_invalid_ticket(string how)   // criterion 13
    {
        var (userId, session) = await AccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), how switch { "sign_in" => "", "reauth" => "?purpose=reauth", _ => "?purpose=connect" });
        var value = ticket.Value;
        string? binding = ticket.Binding;
        switch (how)
        {
            case "unknown":
                value = "unknownunknown" + ticket.Value[14..];
                break;
            case "used":
                using (var first = await SettingsApi.Connect(Client, session, ticket))
                {
                    Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
                }

                await InDbAsync(db => db.UserLogins.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
                break;
            case "expired":
                Clock.Advance(TimeSpan.FromMinutes(10));
                session = await SessionApi.RefreshOk(Client, session.RefreshToken);
                using (var confirmed = await SettingsApi.Confirm(Client, session, new { password = UserPassword }))
                {
                    Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);   // the window is open again: only the ticket is dead
                }

                break;
            case "no-binding":
                binding = null;
                break;
            case "another-binding":
                binding = (await TicketAsync("microsoft", PersonalMicrosoft())).Binding;
                break;
            case "blank":
                value = "";
                break;
        }

        var before = await SnapshotAsync();
        using var response = await SettingsApi.Post(Client, SettingsApi.ConnectPath, session, new { ticket = value }, binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Null(ProviderApi.BindingCookieOf(response));   // auth_pt is not cleared: the browser keeps what it has
        Assert.Equal(before, await SnapshotAsync());   // nothing used up, written or queued
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Equal(how == "used" ? 1 : 0, (await AuditAsync(AuditKinds.ProviderLinked)).Count);   // the first connect's row is the only one
    }

    [Fact]
    public async Task A_closed_window_is_403_confirmation_required_and_the_ticket_stays_usable()   // criterion 13, Review Focus 5
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();
        Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var before = await SnapshotAsync();

        using var refused = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.Forbidden, "confirmation_required");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Null(ProviderApi.BindingCookieOf(refused));
        Assert.Equal(before, await SnapshotAsync());

        using (var confirmed = await SettingsApi.Confirm(Client, session, new { password = UserPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        using var again = await SettingsApi.Connect(Client, session, ticket);   // the same ticket, sent again
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Single(await LoginsOfAsync(userId));
    }

    [Theory]
    [InlineData("linked")]
    [InlineData("other-account")]
    [InlineData("in-use")]
    [InlineData("no-email")]
    [InlineData("unverified")]
    [InlineData("mismatch")]
    public async Task A_closed_window_is_answered_before_every_later_check(string how)   // criterion 13: step 4 before steps 5 to 8
    {
        var (userId, session) = await AccountAsync();
        var kim = await CreateUserAsync(Kim, confirmed: true);
        switch (how)
        {
            case "linked":
                await LinkAsync(userId, "google", FakePeople.GoogleSubject);
                break;
            case "other-account":
                await LinkAsync(kim.Id, "google", FakePeople.GoogleSubject);
                break;
            case "in-use":
                await LinkAsync(userId, "google", OtherGoogleSubject);
                break;
        }

        var ticket = await ConnectTicketAsync(how switch
        {
            "no-email" => GooglePerson(defect: FakeDefects.NoEmail),
            "unverified" => GooglePerson(verified: false),
            "mismatch" => GooglePerson(email: "ola.work@gmail.test", subject: OtherGoogleSubject),
            _ => GooglePerson(),
        });
        Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var before = await SnapshotAsync();

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "confirmation_required");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?purpose=reauth")]
    public async Task A_ticket_of_another_purpose_is_refused_before_the_window_and_before_the_logins_are_judged(string query)   // criterion 13: step 3 before steps 4 to 7
    {
        var (_, session) = await AccountAsync();
        var kim = await CreateUserAsync(Kim, confirmed: true);
        await LinkAsync(kim.Id, "google", FakePeople.GoogleSubject);   // a connect ticket of this identity would be 409 provider_already_linked
        var ticket = await TicketAsync("google", GooglePerson(), query);
        Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));   // and, with the window closed, 403 confirmation_required
        var before = await SnapshotAsync();

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task An_identity_that_is_already_this_accounts_login_is_204_the_ticket_used_up_and_nothing_written()   // criterion 13, step 5
    {
        var (userId, session) = await AccountAsync();
        await LinkAsync(userId, "google", FakePeople.GoogleSubject);
        var ticket = await ConnectTicketAsync();
        var before = await SnapshotAsync();

        using var response = await SettingsApi.Connect(Client, session, ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal(before with { Tickets = 0 }, await SnapshotAsync());   // the ticket is the only thing that changed
        Assert.Single(await LoginsOfAsync(userId));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(0, await NoticeCountAsync());   // no mail for a connect of an identity already linked
    }

    [Fact]
    public async Task An_identity_of_another_account_is_409_provider_already_linked()   // criterion 13, step 6
    {
        var (userId, session) = await AccountAsync();
        var kim = await CreateUserAsync(Kim, confirmed: true);
        await LinkAsync(kim.Id, "google", FakePeople.GoogleSubject);
        await LinkAsync(userId, "google", OtherGoogleSubject);   // step 6 comes before step 7
        var ticket = await ConnectTicketAsync();
        var before = await SnapshotAsync();

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "provider_already_linked");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task An_account_with_a_login_of_the_provider_is_409_provider_in_use()   // criterion 13, step 7
    {
        var (userId, session) = await AccountAsync();
        await LinkAsync(userId, "google", OtherGoogleSubject);
        var ticket = await ConnectTicketAsync(GooglePerson(verified: false));   // step 7 comes before step 8
        var before = await SnapshotAsync();

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "provider_in_use");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(OtherGoogleSubject, Assert.Single(await LoginsOfAsync(userId)).ProviderKey);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("no-email", "no_email")]
    [InlineData("unverified", "email_not_verified")]
    public async Task A_ticket_without_a_verified_address_is_refused_and_the_ticket_stays(string how, string code)   // criterion 13, step 8
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync(how == "no-email" ? GooglePerson(defect: FakeDefects.NoEmail) : GooglePerson(verified: false));
        var before = await SnapshotAsync();

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, code);
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("ola.work@gmail.test")]
    [InlineData("o1a@gmail.test")]
    [InlineData("o.la@gmail.test")]
    public async Task An_address_that_is_not_the_accounts_is_403_address_mismatch_and_the_ticket_stays(string address)   // criterion 13, Review Focus 4
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync(GooglePerson(email: address, subject: OtherGoogleSubject));
        var before = await SnapshotAsync();

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "address_mismatch");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task An_unverified_address_that_is_also_not_the_accounts_is_email_not_verified_not_address_mismatch()   // criterion 13, step 8: the order of its checks
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync(GooglePerson(email: "ola.work@gmail.test", verified: false, subject: OtherGoogleSubject));

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "email_not_verified");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
    }

    [Fact]
    public async Task The_same_address_in_another_ascii_case_connects()   // EmailInput.IsSameAddress
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync(GooglePerson(email: "OLA@Gmail.Test"));

        using var response = await SettingsApi.Connect(Client, session, ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(await LoginsOfAsync(userId));
    }

    [Fact]
    public async Task The_checks_run_in_the_order_of_the_contract()   // guard lesson 10: one pair of adjacent steps at a time
    {
        var (userId, session) = await AccountAsync();
        var kim = await CreateUserAsync(Kim, confirmed: true);
        await LinkAsync(kim.Id, "google", FakePeople.GoogleSubject);
        var ticket = await ConnectTicketAsync();

        // 3 before 4: a dead ticket and a closed window are 400.
        Clock.Advance(TimeSpan.FromMinutes(6));
        session = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using (var dead = await SettingsApi.Post(Client, SettingsApi.ConnectPath, session, new { ticket = "nope" }, ticket.Binding))
        {
            await TenancyApi.AssertErrorAsync(dead, HttpStatusCode.BadRequest, "invalid_ticket");
        }

        // 4 before 6: another account's identity and a closed window are 403 confirmation_required.
        using (var closed = await SettingsApi.Connect(Client, session, ticket))
        {
            await TenancyApi.AssertErrorAsync(closed, HttpStatusCode.Forbidden, "confirmation_required");
        }

        // 2 before 3: no membership and a dead ticket are 403 no_membership.
        await RemoveMembershipAsync(userId);
        using var noMembership = await SettingsApi.Post(Client, SettingsApi.ConnectPath, session, new { ticket = "nope" }, null);
        await TenancyApi.AssertErrorAsync(noMembership, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(1, await TicketCountAsync());
    }

    public static TheoryData<string> BadBodies => new() { "not json", "{}", """["ticket"]""", """{"ticket":5}""", """{"ticket":null}""" };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request_before_the_session(string body)   // criterion 13, step 1
    {
        var (_, session) = await AccountAsync();

        using var response = await SettingsApi.PostRaw(Client, SettingsApi.ConnectPath, session with { RefreshToken = "" }, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Without_the_refresh_cookie_it_is_401_session_required_and_without_a_token_the_plain_401()   // step 2
    {
        var (_, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();

        using var noCookie = await SettingsApi.PostAs(Client, SettingsApi.ConnectPath, session.AccessToken, null, new { ticket = ticket.Value }, ticket.Binding);
        using var noToken = await SettingsApi.PostAs(Client, SettingsApi.ConnectPath, null, session.RefreshToken, new { ticket = ticket.Value }, ticket.Binding);

        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");
        await TenancyApi.AssertUnauthorizedAsync(noToken);
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task A_connect_ticket_of_a_provider_dropped_after_its_callback_is_honoured()   // criterion 13, spec 0012 → Provider tickets
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();
        SetManifest(AuthAppFactory.SelfServiceManifest + AuthAppFactory.PrivateBlock + "\nproviders: [microsoft]\n", withProviders: false);   // Google is no longer offered

        using var response = await SettingsApi.Connect(Client, session, ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(await LoginsOfAsync(userId));
    }

    [Fact]
    public async Task Two_connects_of_one_identity_at_once_add_one_login_and_both_answer_204()   // criterion 13, with parallel requests
    {
        var (userId, session) = await AccountAsync();
        var first = await ConnectTicketAsync();
        var second = await ConnectTicketAsync();   // another browser of the same person: its own auth_pt

        Task<HttpResponseMessage>[] pending;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            pending = [SettingsApi.Connect(Client, session, first), SettingsApi.Connect(Client, session, second)];
            await HeldLock.WaitForWaitersAsync(Factory, 2, pending);
            await held.ReleaseAsync();
        }

        var answers = await Task.WhenAll(pending);
        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode));
        Assert.Single(await LoginsOfAsync(userId));
        Assert.Single(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(1, await NoticeCountAsync());
        Assert.Equal(0, await TicketCountAsync());
        foreach (var answer in answers)
        {
            answer.Dispose();
        }
    }

    [Fact]
    public async Task Two_connects_of_two_identities_of_one_provider_at_once_add_one_and_the_other_is_409_provider_in_use()   // criterion 13
    {
        var (userId, session) = await AccountAsync();
        var first = await ConnectTicketAsync();
        var second = await ConnectTicketAsync(GooglePerson(subject: OtherGoogleSubject));

        Task<HttpResponseMessage>[] pending;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            pending = [SettingsApi.Connect(Client, session, first), SettingsApi.Connect(Client, session, second)];
            await HeldLock.WaitForWaitersAsync(Factory, 2, pending);
            await held.ReleaseAsync();
        }

        var answers = await Task.WhenAll(pending);
        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], answers.Select(a => a.StatusCode).Order());
        await TenancyApi.AssertErrorAsync(answers.Single(a => a.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "provider_in_use");
        Assert.Single(await LoginsOfAsync(userId));
        Assert.Equal(1, await TicketCountAsync());   // the refused one is given back
        Assert.Single(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(1, await NoticeCountAsync());
        foreach (var answer in answers)
        {
            answer.Dispose();
        }
    }

    [Fact]
    public async Task Two_connects_with_one_ticket_add_one_login_and_the_other_is_400_invalid_ticket()   // guard lesson 1: one ticket, two requests
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();

        Task<HttpResponseMessage>[] pending;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            pending = [SettingsApi.Connect(Client, session, ticket), SettingsApi.Connect(Client, session, ticket)];
            await HeldLock.WaitForWaitersAsync(Factory, 2, pending);
            await held.ReleaseAsync();
        }

        var answers = await Task.WhenAll(pending);
        try
        {
            Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.BadRequest], answers.Select(a => a.StatusCode).Order());
            var refused = answers.Single(a => a.StatusCode == HttpStatusCode.BadRequest);
            await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.BadRequest, "invalid_ticket");
            Assert.Null(ProviderApi.BindingCookieOf(refused));
            ProviderApi.AssertBindingCleared(answers.Single(a => a.StatusCode == HttpStatusCode.NoContent));
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }

        Assert.Single(await LoginsOfAsync(userId));
        Assert.Single(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(1, await NoticeCountAsync());
        Assert.Equal(0, await TicketCountAsync());
    }

    [Fact]
    public async Task Two_accounts_with_two_addresses_connecting_one_identity_at_once_add_it_to_one_and_the_other_is_409_provider_already_linked()   // the identity lock
    {
        var (olaId, olaSession) = await AccountAsync();
        var kimId = (await CreateUserAsync(Kim, confirmed: true)).Id;
        var kimSession = await SessionApi.LoginAsync(Client, Kim, UserPassword);
        var olasTicket = await ConnectTicketAsync();
        var kimsTicket = await ConnectTicketAsync(GooglePerson(email: Kim));   // the same identity, the address Google says now: Kim's

        // Each takes the lock of its own address, so only the identity lock makes the second wait: the test holds it, and both queue behind it.
        Task<HttpResponseMessage>[] pending;
        await using (var held = await HeldLock.IdentityAsync(Factory, "google", FakePeople.GoogleSubject))
        {
            pending = [SettingsApi.Connect(Client, olaSession, olasTicket), SettingsApi.Connect(Client, kimSession, kimsTicket)];
            await HeldLock.WaitForWaitersAsync(Factory, 2, pending);
            await held.ReleaseAsync();
        }

        var answers = await Task.WhenAll(pending);
        try
        {
            Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], answers.Select(a => a.StatusCode).Order());
            await TenancyApi.AssertErrorAsync(answers.Single(a => a.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "provider_already_linked");
            var winner = answers[0].StatusCode == HttpStatusCode.NoContent ? olaId : kimId;
            Assert.Equal(winner, Assert.Single(await InDbAsync(db => db.UserLogins.ToListAsync(TestContext.Current.CancellationToken))).UserId);
            Assert.Equal(1, await TicketCountAsync());   // the loser's ticket is given back
            Assert.Equal(winner, (await SingleAsync(AuditKinds.ProviderLinked)).SubjectUserId);
            Assert.Equal(1, await NoticeCountAsync());
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_ticket_used_up_while_the_connect_waited_for_the_lock_is_400_invalid_ticket()   // guard lesson 1
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();
        var before = await SnapshotAsync() with { Tickets = 0 };

        using var response = await ConnectWhileLockedAsync(
            session, ticket, () => InDbAsync(db => db.ProviderTickets.ExecuteDeleteAsync(TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task A_ticket_that_expired_while_the_connect_waited_for_the_lock_is_400_invalid_ticket()   // guard lesson 11: the clock is read after the lock
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();   // lives 10 minutes; the window of the login ends sooner, so the window is opened again below
        Clock.Advance(TimeSpan.FromMinutes(9));
        using (var confirmed = await SettingsApi.Confirm(Client, session, new { password = UserPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        var before = await SnapshotAsync();

        // The ticket and the window are live when the request arrives; the ticket ends while it waits, the window (until minute 14) does not.
        using var response = await ConnectWhileLockedAsync(session, ticket, () => { Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1)); return Task.CompletedTask; });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Equal(before, await SnapshotAsync());   // a refusal deletes nothing
    }

    [Fact]
    public async Task A_window_that_ended_while_the_connect_waited_is_403_confirmation_required_and_the_ticket_is_given_back()   // under the lock
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();
        var before = await SnapshotAsync();

        // The window (until minute 5) ends while the request waits; the ticket (until minute 10) does not.
        using var response = await ConnectWhileLockedAsync(session, ticket, () => { Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)); return Task.CompletedTask; });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "confirmation_required");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Equal(before, await SnapshotAsync());

        using (var confirmed = await SettingsApi.Confirm(Client, session, new { password = UserPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        using var again = await SettingsApi.Connect(Client, session, ticket);   // the ticket was given back by the rollback
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task A_membership_removed_while_the_connect_waited_is_403_no_membership_and_the_ticket_stays()   // under the lock
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();
        var before = await SnapshotAsync();

        using var response = await ConnectWhileLockedAsync(session, ticket, () => RemoveMembershipAsync(userId));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task A_session_ended_while_the_connect_waited_is_401_session_required_and_the_ticket_stays()   // under the lock
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();
        var before = await SnapshotAsync();

        using var response = await ConnectWhileLockedAsync(
            session,
            ticket,
            async () =>
            {
                using var logout = await SessionApi.Logout(Client, session.RefreshToken);
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Equal(before.LinkedRows, (await AuditAsync(AuditKinds.ProviderLinked)).Count);
        Assert.Equal(before.Mails, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task An_identity_linked_to_another_account_while_the_connect_waited_is_409_provider_already_linked_and_the_ticket_stays()   // under the lock, step 6
    {
        var (userId, session) = await AccountAsync();
        var kim = await CreateUserAsync(Kim, confirmed: true);
        var ticket = await ConnectTicketAsync();

        using var response = await ConnectWhileLockedAsync(session, ticket, () => LinkAsync(kim.Id, "google", FakePeople.GoogleSubject));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "provider_already_linked");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Single(await LoginsOfAsync(kim.Id));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(0, await NoticeCountAsync());
    }

    [Fact]
    public async Task A_login_of_the_provider_added_while_the_connect_waited_is_409_provider_in_use_and_the_ticket_stays()   // under the lock, step 7
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();

        using var response = await ConnectWhileLockedAsync(session, ticket, () => LinkAsync(userId, "google", OtherGoogleSubject));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "provider_in_use");
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(OtherGoogleSubject, Assert.Single(await LoginsOfAsync(userId)).ProviderKey);
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(0, await NoticeCountAsync());
    }

    [Fact]
    public async Task The_identity_made_this_accounts_login_while_the_connect_waited_is_204_the_ticket_used_up_and_nothing_else_written()   // under the lock, step 5
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();

        using var response = await ConnectWhileLockedAsync(session, ticket, () => LinkAsync(userId, "google", FakePeople.GoogleSubject));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal(0, await TicketCountAsync());
        Assert.Single(await LoginsOfAsync(userId));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(0, await NoticeCountAsync());
    }

    [Fact]
    public async Task No_ticket_binding_cookie_or_token_reaches_the_audit_log_or_the_log()   // guard lesson 4
    {
        var (_, session) = await AccountAsync();
        List<string> bodies = [];
        var refusedTicket = await ConnectTicketAsync(GooglePerson(email: "ola.work@gmail.test", subject: OtherGoogleSubject));
        using (var refused = await SettingsApi.Connect(Client, session, refusedTicket))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            bodies.Add(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var ticket = await ConnectTicketAsync();
        using (var response = await SettingsApi.Connect(Client, session, ticket))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        string[] secrets = [ticket.Value, ticket.Binding, refusedTicket.Value, refusedTicket.Binding, session.RefreshToken, session.AccessToken];
        await AssertNoSecretsAsync(secrets);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal));
        Assert.All(secrets, secret => Assert.All(bodies, body => Assert.DoesNotContain(secret, body, StringComparison.Ordinal)));
    }
}
