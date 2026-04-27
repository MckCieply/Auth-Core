using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Seeding;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>POST /auth/providers/link</c> (criteria 15 to 17).</summary>
public sealed class ProviderLinkTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;

    // Spelled out, so that the escape in a test body is not read as an escape of this file.
    private const char Backslash = (char)0x5C;
    private const char Mu = (char)0x03BC;
    private const char Micro = (char)0x00B5;

    private Task<List<IdentityUserLogin<Guid>>> LoginsAsync() =>
        InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    private Task<int> AttemptsOfAsync(string normalizedEmail) =>
        InDbAsync(db => db.LoginStreaks.Where(s => s.IdentifierHash == LoginIdentifier.HashOf(normalizedEmail)).Select(s => s.AttemptCount)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken));

    private Task<int> StreakRowsAsync() => InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>The password, the ticket, the cookie and the tokens appear in no log line, no audit row and no body given.</summary>
    private async Task AssertNoSecretsAnywhereAsync(string[] secrets, params string[] bodies)
    {
        await AssertNoSecretsAsync(secrets);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal);
            Assert.All(bodies, body => Assert.DoesNotContain(secret, body, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_right_password_links_and_signs_in_and_the_provider_then_signs_in_alone()   // criterion 15
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);

        var session = await SessionApi.ReadSessionAsync(linked);
        Assert.Equal(user.Id.ToString(), AccessTokens.Text(session.AccessToken, "sub"));
        ProviderApi.AssertBindingCleared(linked);
        Assert.Equal(0, await TicketCountAsync());
        var login = Assert.Single(await LoginsAsync());
        Assert.Equal(("google", FakePeople.GoogleSubject, user.Id), (login.LoginProvider, login.ProviderKey, login.UserId));
        var row = await SingleAsync(AuditKinds.ProviderLinked);
        Assert.Equal(user.Id, row.ActorUserId);
        Assert.Equal(user.Id, row.SubjectUserId);
        Assert.Equal(Ola, row.SubjectEmail);
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal(DevUserSeeder.DefaultOrgName, row.OrgName);
        Assert.Equal(["provider"], AuditApi.DetailNames(row));
        Assert.Equal("google", AuditApi.Text(row, "provider"));
        Assert.Equal("google", AuditApi.Text(await SingleAsync(AuditKinds.LoginSucceeded), "provider"));

        // From then on the provider alone signs in, and the password still does.
        var again = await TicketAsync("google", GooglePerson());
        using (var exchanged = await ProviderApi.Exchange(Client, again))
        {
            Assert.Equal(user.Id.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(exchanged)).AccessToken, "sub"));
        }

        _ = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        await AssertNoSecretsAnywhereAsync([UserPassword, ticket.Value, ticket.Binding, session.AccessToken, session.RefreshToken]);
    }

    [Fact]
    public async Task A_link_whose_rows_cannot_be_written_links_nothing_and_leaves_the_ticket()   // criterion 15: one transaction
    {
        var user = await CreateUserAsync(Ola, confirmed: false);   // the confirmation is part of the same change
        var ticket = await TicketAsync("google", GooglePerson());
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.InternalServerError, linked.StatusCode);
        Assert.Empty(await LoginsAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Null(ProviderApi.BindingCookieOf(linked));
        Assert.False(await InDbAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.EmailConfirmed).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_wrong_password_and_an_address_without_an_account_answer_alike_and_both_count()   // criterion 16
    {
        await CreateUserAsync(Ola, confirmed: true);
        var known = await TicketAsync("google", GooglePerson());
        var unknown = await TicketAsync("google", GooglePerson(email: "nobody@gmail.test", subject: "400000000000000000004"));

        using var wrong = await ProviderApi.Link(Client, known, "Wrong-Passw0rd");
        using var nobody = await ProviderApi.Link(Client, unknown, UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, nobody.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await wrong.Content.ReadAsStringAsync());
        Assert.Equal("""{"error":"invalid_credentials"}""", await nobody.Content.ReadAsStringAsync());
        Assert.Equal(LoginApi.HeaderNames(wrong), LoginApi.HeaderNames(nobody));
        Assert.Equal(1, await AttemptsOfAsync("OLA@GMAIL.TEST"));
        Assert.Equal(1, await AttemptsOfAsync("NOBODY@GMAIL.TEST"));
        var failures = await AuditAsync(AuditKinds.LoginFailed);
        Assert.Contains(failures, r => AuditApi.Text(r, "reason") == "wrong_password" && AuditApi.Text(r, "provider") == "google" && r.SubjectEmail == Ola);
        Assert.Contains(failures, r => AuditApi.Text(r, "reason") == "unknown_address" && AuditApi.Text(r, "provider") == "google" && r.SubjectUserId is null);
        Assert.Empty(await LoginsAsync());
    }

    [Fact]
    public async Task An_account_without_a_password_answers_as_an_unknown_address()   // criterion 16, Settled 14
    {
        await PasswordlessAccountAsync(Ola);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown_address", AuditApi.Text(await SingleAsync(AuditKinds.LoginFailed), "reason"));
    }

    [Fact]
    public async Task A_wrong_password_leaves_the_ticket_usable_and_the_right_one_then_links()   // criterion 16, Review Focus 4
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var wrong = await ProviderApi.Link(Client, ticket, "Wrong-Passw0rd");
        using var right = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Null(ProviderApi.BindingCookieOf(wrong));
        await AssertNoSecretsAnywhereAsync([ticket.Value, ticket.Binding, "Wrong-Passw0rd"], await wrong.Content.ReadAsStringAsync());
        Assert.Equal(user.Id.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(right)).AccessToken, "sub"));
        Assert.Equal(0, await AttemptsOfAsync("OLA@GMAIL.TEST"));   // the right password ends the streak
    }

    [Fact]
    public async Task An_unconfirmed_account_is_confirmed_when_the_address_is_verified()   // criterion 17, Settled 13
    {
        var user = await CreateUserAsync(Ola, confirmed: false);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await InDbAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.EmailConfirmed).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task An_unconfirmed_account_with_an_unverified_address_is_403_email_not_verified()   // criterion 17
    {
        var user = await CreateUserAsync(FakePeople.WorkEmail, confirmed: false);
        var ticket = await TicketAsync("microsoft", WorkMicrosoft());

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        await AssertNoSecretsAnywhereAsync([UserPassword, ticket.Value, ticket.Binding], await response.Content.ReadAsStringAsync());
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "email_not_verified");
        Assert.False(await InDbAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.EmailConfirmed).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Empty(await LoginsAsync());
        Assert.Equal(1, await TicketCountAsync());
        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Equal("unconfirmed_address", AuditApi.Text(row, "reason"));
        Assert.Equal("microsoft", AuditApi.Text(row, "provider"));
    }

    [Fact]
    public async Task An_account_under_a_look_alike_spelling_is_401_as_an_unknown_address_and_nothing_is_linked_or_confirmed()   // spec 0011 → link, step 6
    {
        var user = await CreateUserAsync(Mu + "aria@firma.test", confirmed: false);
        var ticket = await TicketAsync("google", GooglePerson(email: Micro + "aria@firma.test", subject: "300000000000000000003"));

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await response.Content.ReadAsStringAsync());
        Assert.Empty(await LoginsAsync());
        Assert.False(await InDbAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.EmailConfirmed).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(1, await StreakRowsAsync());
        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Equal("unknown_address", AuditApi.Text(row, "reason"));
        Assert.Null(row.SubjectUserId);
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
    }

    [Fact]
    public async Task An_account_stored_with_other_letter_case_than_the_ticket_address_links()   // spec 0011 → link, step 6: the same up to ASCII case
    {
        var user = await CreateUserAsync("Ola@Gmail.test", confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());   // ola@gmail.test

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(user.Id.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(response)).AccessToken, "sub"));
        Assert.Equal(user.Id, Assert.Single(await LoginsAsync()).UserId);
        Assert.Equal("Ola@Gmail.test", (await SingleAsync(AuditKinds.ProviderLinked)).SubjectEmail);
    }

    [Fact]
    public async Task A_ticket_used_up_by_a_link_is_400_invalid_ticket_at_the_next_link_and_counts_for_nothing()   // spec 0011 → link, step 2
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        using (var first = await ProviderApi.Link(Client, ticket, UserPassword))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var again = await ProviderApi.Link(Client, ticket, UserPassword);

        await TenancyApi.AssertErrorAsync(again, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(0, await StreakRowsAsync());
    }

    [Fact]
    public async Task A_ticket_without_its_cookie_or_with_another_browsers_is_400_invalid_ticket_and_stays()   // spec 0011 → link, step 2
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        var other = await TicketAsync("google", GooglePerson());

        using var without = await ProviderApi.PostWithBinding(Client, ProviderApi.LinkPath, new { ticket = ticket.Value, password = UserPassword }, null);
        using var foreign = await ProviderApi.PostWithBinding(Client, ProviderApi.LinkPath, new { ticket = ticket.Value, password = UserPassword }, other.Binding);

        await TenancyApi.AssertErrorAsync(without, HttpStatusCode.BadRequest, "invalid_ticket");
        await TenancyApi.AssertErrorAsync(foreign, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(2, await TicketCountAsync());
        Assert.Equal(0, await StreakRowsAsync());
        Assert.Empty(await LoginsAsync());
    }

    [Fact]
    public async Task An_expired_ticket_is_400_invalid_ticket_and_counts_for_nothing()   // spec 0011 → link, step 2
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        Clock.Advance(TimeSpan.FromMinutes(11));

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(0, await StreakRowsAsync());
        Assert.Empty(await LoginsAsync());
    }

    public static TheoryData<string> BadBodies => new()
    {
        "not json",
        "{}",
        """["ticket","password"]""",
        """{"password":"Another-Passw0rd"}""",
        """{"ticket":"x"}""",
        """{"ticket":"x","password":""}""",
        """{"ticket":"x","password":"   "}""",
        """{"ticket":"x","password":null}""",
        """{"ticket":"x","password":5}""",
        """{"ticket":5,"password":"Another-Passw0rd"}""",
        "{\"ticket\":\"x\",\"password\":\"Another" + Backslash + "u0000Passw0rd\"}",   // NUL in the password
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request_and_counts_for_nothing(string body)   // criterion 17
    {
        using var response = await ProviderApi.PostRawWithBinding(Client, ProviderApi.LinkPath, body, "any-binding");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("""{"ticket":"","password":"Another-Passw0rd"}""")]
    [InlineData("""{"ticket":"   ","password":"Another-Passw0rd"}""")]
    public async Task A_blank_ticket_is_400_invalid_ticket_and_counts_for_nothing(string body)   // spec 0011 → link: a blank ticket is a string, so step 2
    {
        using var response = await ProviderApi.PostRawWithBinding(Client, ProviderApi.LinkPath, body, "any-binding");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_ticket_of_another_purpose_is_400_invalid_ticket()   // criterion 17
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
    }

    [Fact]
    public async Task An_identity_linked_meanwhile_signs_in_without_judging_the_password()   // criterion 17, spec 0011 → link, step 3
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);   // another tab linked it

        using var response = await ProviderApi.Link(Client, ticket, "Wrong-Passw0rd");

        Assert.Equal(user.Id.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(response)).AccessToken, "sub"));
        Assert.Empty(await AuditAsync(AuditKinds.LoginFailed));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(0, await AttemptsOfAsync("OLA@GMAIL.TEST"));
    }

    [Fact]
    public async Task A_ticket_without_an_address_is_403_no_email()   // spec 0011 → link, step 4
    {
        var ticket = await TicketAsync("google", GooglePerson(defect: FakeDefects.NoEmail));

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_email");
    }

    [Fact]
    public async Task An_account_without_a_membership_is_403_no_membership()   // spec 0011 → link, step 7
    {
        await CreateUserAsync(Ola, confirmed: true, member: false);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal("no_company", AuditApi.Text(await SingleAsync(AuditKinds.LoginFailed), "reason"));
        Assert.Empty(await LoginsAsync());
    }
}
