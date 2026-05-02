using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>POST /auth/providers/finish</c> (criteria 18 and 19).</summary>
public sealed class ProviderFinishTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string NewPerson = "new@gmail.test";
    private const string NewSubject = "500000000000000000005";
    private const char Mu = (char)0x03BC;
    private const char Micro = (char)0x00B5;

    private Task<ProviderApi.Ticket> NewTicketAsync(string email = NewPerson) => TicketAsync("google", GooglePerson(email: email, subject: NewSubject));

    private Task<int> AccountsAsync(string normalized) =>
        InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == normalized, TestContext.Current.CancellationToken));

    private Task<int> LoginsAsync() => InDbAsync(db => db.UserLogins.CountAsync(TestContext.Current.CancellationToken));

    private sealed record Counts(int Accounts, int Companies, int Memberships, int Logins, int Tickets, int Pending, int AuditRows);

    private Task<Counts> CountsAsync(bool withAudit = true) =>
        InDbAsync(async db =>
        {
            var cancellation = TestContext.Current.CancellationToken;
            return new Counts(
                await db.Users.CountAsync(cancellation),
                await db.Companies.CountAsync(cancellation),
                await db.Memberships.CountAsync(cancellation),
                await db.UserLogins.CountAsync(cancellation),
                await db.ProviderTickets.CountAsync(cancellation),
                await db.PendingSignups.CountAsync(cancellation),
                withAudit ? await db.AuditEvents.CountAsync(cancellation) : -1);
        });

    /// <summary>A refused finish changes nothing: the rows counted before (the audit rows too) are the rows now, and no sign-up or sign-in is written.</summary>
    private async Task AssertNothingChangedAsync(Counts before)
    {
        Assert.Equal(before, await CountsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.SignupCompleted));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
        Assert.DoesNotContain(await AuditAsync(AuditKinds.OrgCreated), row => AuditApi.Text(row, "via") == "signup");
    }

    /// <summary>The ticket, its binding and the tokens appear in no log line, no audit row and no body given.</summary>
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
    public async Task A_private_finish_makes_an_account_without_a_password_with_its_login_and_a_personal_space_and_signs_in()   // criterion 18
    {
        await PendingSignupSeed.IssueAsync(Factory.Services, NewPerson, null, SignupKind.Private);   // a sign-up through the mail, asked for too
        await PendingSignupSeed.IssueAsync(Factory.Services, "other@firma.test", "Firma");   // another address's is left alone
        var ticket = await NewTicketAsync();
        var cancellation = TestContext.Current.CancellationToken;

        using var response = await ProviderApi.Finish(Client, ticket, kind: "private");

        var raw = await response.Content.ReadAsStringAsync(cancellation);
        var session = await SessionApi.ReadSessionAsync(response);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal("personal", AccessTokens.Text(session.AccessToken, "org_kind"));
        var account = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "NEW@GMAIL.TEST", cancellation));
        Assert.Equal(account.Id.ToString(), AccessTokens.Text(session.AccessToken, "sub"));
        Assert.Null(account.PasswordHash);
        Assert.True(account.EmailConfirmed);
        Assert.Equal(NewPerson, account.Email);
        var login = Assert.Single(await InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(cancellation)));
        Assert.Equal(("google", NewSubject, account.Id), (login.LoginProvider, login.ProviderKey, login.UserId));
        var space = await InDbAsync(db => (
            from membership in db.Memberships
            join company in db.Companies on membership.CompanyId equals company.Id
            where membership.UserId == account.Id
            select company).AsNoTracking().SingleAsync(cancellation));
        Assert.Equal(CompanyKind.Personal, space.Kind);
        Assert.Equal("OTHER@FIRMA.TEST", Assert.Single(await PendingAsync()).NormalizedEmail);
        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal(["kind", "provider"], AuditApi.DetailNames(completed));
        Assert.Equal("private", AuditApi.Text(completed, "kind"));
        Assert.Equal("google", AuditApi.Text(completed, "provider"));
        Assert.Equal(account.Id, completed.SubjectUserId);
        Assert.Equal(NewPerson, completed.SubjectEmail);
        Assert.Null(completed.ActorUserId);
        Assert.Equal(space.Id, completed.OrgId);
        Assert.Equal(PersonalSpaces.Name, completed.OrgName);
        var signedIn = await SingleAsync(AuditKinds.LoginSucceeded);
        Assert.Equal("google", AuditApi.Text(signedIn, "provider"));
        Assert.Equal(account.Id, signedIn.SubjectUserId);
        Assert.DoesNotContain(await AuditAsync(AuditKinds.OrgCreated), row => AuditApi.Text(row, "via") == "signup");
        Assert.Equal(0, await TicketCountAsync());
        _ = await SessionApi.RefreshOk(Client, session.RefreshToken);   // the cookie refreshes like a login's
        using var password = await LoginApi.Login(Client, NewPerson, "Any-Passw0rd-1");
        Assert.Equal(HttpStatusCode.Unauthorized, password.StatusCode);   // no password: the 401 of an unknown address
        await AssertNoSecretsAnywhereAsync([ticket.Value, ticket.Binding, session.AccessToken, session.RefreshToken], raw.Replace(session.AccessToken, "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_organization_finish_makes_the_company_with_the_default_roles_and_the_first_admins_role()   // criterion 18
    {
        await PendingSignupSeed.IssueAsync(Factory.Services, NewPerson, "Firma Poczta");
        var ticket = await NewTicketAsync();
        var cancellation = TestContext.Current.CancellationToken;

        using var response = await ProviderApi.Finish(Client, ticket, kind: "organization", companyName: "Firma Nowa");

        var session = await SessionApi.ReadSessionAsync(response);
        Assert.Equal("organization", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        var company = await InDbAsync(db => db.Companies.AsNoTracking().SingleAsync(c => c.Name == "Firma Nowa", cancellation));
        Assert.Equal(company.Id.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(
            ["admin", "user"],
            await InDbAsync(db => db.CompanyRoles.Where(r => r.CompanyId == company.Id).Select(r => r.Name).OrderBy(n => n).ToListAsync(cancellation)));
        var account = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "NEW@GMAIL.TEST", cancellation));
        Assert.Null(account.PasswordHash);
        Assert.True(account.EmailConfirmed);
        var created = Assert.Single(await AuditAsync(AuditKinds.OrgCreated), row => AuditApi.Text(row, "via") == "signup");
        Assert.Equal(company.Id, created.OrgId);
        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal("organization", AuditApi.Text(completed, "kind"));
        Assert.Equal("google", AuditApi.Text(completed, "provider"));
        Assert.Equal(company.Id, completed.OrgId);
        Assert.Equal("Firma Nowa", completed.OrgName);
        Assert.Empty(await PendingAsync());   // the pending sign-up of the address, of the other kind, went too
        Assert.Equal(0, await TicketCountAsync());
    }

    [Fact]
    public async Task Without_kind_it_is_an_organization()   // spec 0011 → finish, step 2
    {
        var ticket = await NewTicketAsync();

        using var response = await ProviderApi.Finish(Client, ticket, companyName: "Firma Bez Rodzaju");

        Assert.Equal("organization", AccessTokens.Text((await SessionApi.ReadSessionAsync(response)).AccessToken, "org_kind"));
    }

    [Fact]
    public async Task Without_kind_and_without_a_company_name_it_is_400_and_the_ticket_stays()   // spec 0011 → finish, steps 2 and 6
    {
        var ticket = await NewTicketAsync();
        var before = await CountsAsync();

        using var response = await ProviderApi.Finish(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        await AssertNothingChangedAsync(before);
    }

    [Fact]
    public async Task With_both_switches_off_the_finish_is_403_before_the_body_is_read()   // criterion 19
    {
        SetManifest(AuthAppFactory.DefaultManifest);

        using var response = await ProviderApi.PostRawWithBinding(Client, ProviderApi.FinishPath, "not json", null);

        await SignupApi.AssertDisabledAsync(response);
    }

    [Fact]
    public async Task With_both_switches_off_a_good_request_with_a_live_ticket_is_403_and_the_ticket_stays()   // criterion 19
    {
        var ticket = await NewTicketAsync();
        SetManifest(AuthAppFactory.DefaultManifest);
        var before = await CountsAsync();

        using var response = await ProviderApi.Finish(Client, ticket, "private");

        await SignupApi.AssertDisabledAsync(response);
        await AssertNothingChangedAsync(before);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("organization")]
    public async Task A_kind_whose_switch_is_off_is_403_signup_disabled_and_the_ticket_stays(string kind)   // criterion 19
    {
        var ticket = await NewTicketAsync();
        SetManifest(kind == "private" ? AuthAppFactory.SelfServiceManifest : AuthAppFactory.DefaultManifest + AuthAppFactory.PrivateBlock);
        var before = await CountsAsync();

        using var response = await ProviderApi.Finish(Client, ticket, kind, kind == "organization" ? "Firma" : null);

        await SignupApi.AssertDisabledAsync(response);
        await AssertNothingChangedAsync(before);
    }

    [Fact]
    public async Task The_other_kind_still_finishes_while_one_switch_is_off()   // criterion 19
    {
        SetManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.PrivateBlock);
        var ticket = await NewTicketAsync();

        using var response = await ProviderApi.Finish(Client, ticket, "private");

        Assert.Equal("personal", AccessTokens.Text((await SessionApi.ReadSessionAsync(response)).AccessToken, "org_kind"));
    }

    public static TheoryData<string> BadBodies => new()
    {
        "not json",
        "{}",
        """["ticket"]""",
        """{"ticket":5}""",
        """{"ticket":null}""",
        """{"ticket":"x","kind":"company"}""",
        """{"ticket":"x","kind":"Private"}""",
        """{"ticket":"x","kind":5}""",
        """{"ticket":"x","kind":null}""",
        """{"ticket":"x","company_name":" Firma"}""",
        """{"ticket":"x","company_name":""}""",
        """{"ticket":"x","company_name":5}""",
        """{"ticket":"x","company_name":null}""",
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request(string body)   // criterion 19
    {
        using var response = await ProviderApi.PostRawWithBinding(Client, ProviderApi.FinishPath, body, "any-binding");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_bad_body_for_a_live_ticket_leaves_it_usable()   // criterion 19
    {
        var ticket = await NewTicketAsync();

        using var bad = await ProviderApi.Finish(Client, ticket, "private", companyName: " Firma");
        using var good = await ProviderApi.Finish(Client, ticket, "private");

        await TenancyApi.AssertErrorAsync(bad, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Theory]
    [InlineData("""{"ticket":"","kind":"private"}""")]
    [InlineData("""{"ticket":"   ","kind":"private"}""")]
    public async Task A_blank_ticket_is_400_invalid_ticket(string body)   // spec 0011 → finish: a blank ticket is a string, so step 3
    {
        using var response = await ProviderApi.PostRawWithBinding(Client, ProviderApi.FinishPath, body, "any-binding");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
    }

    [Fact]
    public async Task A_blank_ticket_with_a_live_binding_is_invalid_ticket_and_the_live_ticket_stays()   // spec 0011 → finish, step 3
    {
        var live = await NewTicketAsync();

        using var response = await ProviderApi.Finish(Client, live with { Value = " " }, "private");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(1, await TicketCountAsync());
    }

    [Theory]
    [InlineData("organization", null)]
    [InlineData("private", "Firma")]
    public async Task An_organization_without_a_company_name_or_a_private_finish_with_one_is_400_and_the_ticket_stays(string kind, string? name)   // criterion 19
    {
        var ticket = await NewTicketAsync();
        var before = await CountsAsync();

        using var refused = await ProviderApi.Finish(Client, ticket, kind, name);

        await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.BadRequest, "invalid_request");
        await AssertNothingChangedAsync(before);
        using var afterwards = await ProviderApi.Finish(Client, ticket, "private");
        Assert.Equal(HttpStatusCode.OK, afterwards.StatusCode);
    }

    [Fact]
    public async Task An_unverified_or_missing_address_is_403_email_not_verified()   // criterion 19, Review Focus 5
    {
        var unverified = await TicketAsync("microsoft", WorkMicrosoft(email: "new@firma.test", subject: "00000000-0000-0000-0000-00000000b003"));
        var none = await TicketAsync("google", GooglePerson(defect: FakeDefects.NoEmail));
        var before = await CountsAsync();

        using var one = await ProviderApi.Finish(Client, unverified, "private");
        using var two = await ProviderApi.Finish(Client, none, "private");

        await TenancyApi.AssertErrorAsync(one, HttpStatusCode.Forbidden, "email_not_verified");
        await TenancyApi.AssertErrorAsync(two, HttpStatusCode.Forbidden, "email_not_verified");
        Assert.Equal(0, await AccountsAsync("NEW@FIRMA.TEST"));
        await AssertNothingChangedAsync(before);
    }

    [Fact]
    public async Task An_unverified_address_is_403_before_the_switch_of_the_kind_and_the_company_name_are_judged()   // criterion 19: step 4 comes before steps 5 and 6
    {
        var unverified = await TicketAsync("microsoft", WorkMicrosoft(email: "new@firma.test", subject: "00000000-0000-0000-0000-00000000b003"));
        SetManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.PrivateBlock);

        using var switchOff = await ProviderApi.Finish(Client, unverified, "organization", "Firma");
        using var badMembers = await ProviderApi.Finish(Client, unverified, "private", "Firma");

        await TenancyApi.AssertErrorAsync(switchOff, HttpStatusCode.Forbidden, "email_not_verified");
        await TenancyApi.AssertErrorAsync(badMembers, HttpStatusCode.Forbidden, "email_not_verified");
    }

    [Fact]
    public async Task An_unverified_address_that_already_has_an_account_is_403_email_not_verified_and_not_409()   // criterion 19: step 4 comes before step 7, Settled 8
    {
        await CreateUserAsync("new@firma.test", confirmed: true);
        var unverified = await TicketAsync("microsoft", WorkMicrosoft(email: "new@firma.test", subject: "00000000-0000-0000-0000-00000000b003"));

        using var response = await ProviderApi.Finish(Client, unverified, "private");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "email_not_verified");
    }

    [Fact]
    public async Task A_kind_whose_switch_is_off_is_403_before_its_members_are_judged()   // criterion 19: step 5 comes before step 6
    {
        var ticket = await NewTicketAsync();
        SetManifest(AuthAppFactory.SelfServiceManifest);   // organization on, private off

        using var privateWithName = await ProviderApi.Finish(Client, ticket, "private", "Firma");

        await SignupApi.AssertDisabledAsync(privateWithName);
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task A_finish_neither_counts_nor_clears_the_login_streak_of_the_address_and_ends_no_session()   // spec 0011 → Limits and finish: no sessions ended, streak untouched
    {
        var other = await CreateUserAsync("someone@gmail.test", confirmed: true);
        var session = await SessionApi.LoginAsync(Client, "someone@gmail.test", UserPassword);
        await LockoutApi.FailAsync(Client, Clock, NewPerson, 3);   // an address without an account counts too
        var ticket = await NewTicketAsync();

        using var response = await ProviderApi.Finish(Client, ticket, "private");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var attempts = await InDbAsync(db => db.LoginStreaks.Where(s => s.IdentifierHash == LoginIdentifier.HashOf("NEW@GMAIL.TEST"))
            .Select(s => s.AttemptCount).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, attempts);
        _ = await SessionApi.RefreshOk(Client, session.RefreshToken);   // another account's session is as it was
        Assert.NotEqual(other.Id.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(response)).AccessToken, "sub"));
    }

    // Triggers that make the database refuse one write after the finish has done its own work: the sign-in then fails as a whole.
    private const string RefuseLoginSucceeded =
        "CREATE FUNCTION refuse_row() RETURNS trigger AS $$ BEGIN IF TG_TABLE_NAME <> 'audit_events' OR NEW.kind = 'login.succeeded' THEN RAISE EXCEPTION 'refused'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql";

    private const string RefuseAudit = "CREATE TRIGGER refuse_row BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION refuse_row()";
    private const string RefuseTokens = """CREATE TRIGGER refuse_row BEFORE INSERT ON "OpenIddictTokens" FOR EACH ROW EXECUTE FUNCTION refuse_row()""";
    private const string AllowAudit = "DROP TRIGGER refuse_row ON audit_events";
    private const string AllowTokens = """DROP TRIGGER refuse_row ON "OpenIddictTokens" """;

    [Theory]
    [InlineData("audit")]
    [InlineData("tokens")]
    public async Task A_sign_in_that_fails_after_the_account_was_made_rolls_the_whole_finish_back(string which)   // criterion 18: one transaction, the sign-in included
    {
        var ticket = await NewTicketAsync();
        var before = await CountsAsync();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseLoginSucceeded, TestContext.Current.CancellationToken));
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(which == "audit" ? RefuseAudit : RefuseTokens, TestContext.Current.CancellationToken));

        using var response = await ProviderApi.Finish(Client, ticket, "private");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("""{"error":"internal_error"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(response.Headers.Contains("Set-Cookie"));   // no refresh cookie, auth_pt not cleared
        Assert.Equal(0, await AccountsAsync("NEW@GMAIL.TEST"));
        Assert.Equal(before, await CountsAsync());   // no account, space, membership or login; the ticket and the audit rows as they were
        Assert.Empty(await AuditAsync(AuditKinds.SignupCompleted));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(which == "audit" ? AllowAudit : AllowTokens, TestContext.Current.CancellationToken));
        using var again = await ProviderApi.Finish(Client, ticket, "private");   // the ticket is usable still
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Theory]
    [InlineData("company")]
    [InlineData("private")]
    [InlineData("unconfirmed")]
    [InlineData("case")]
    [InlineData("look-alike")]
    public async Task An_address_with_any_account_is_409_already_registered_and_nothing_changes(string which)   // criterion 19
    {
        var typed = NewPerson;
        switch (which)
        {
            case "company":
                await CreateUserAsync(NewPerson, confirmed: true);
                break;
            case "private":
                await CreatePrivateAccountAsync(NewPerson);
                break;
            case "unconfirmed":
                await CreateUserAsync(NewPerson, confirmed: false);
                break;
            case "case":
                await CreateUserAsync(NewPerson, confirmed: true);
                typed = "New@Gmail.test";
                break;
            default:
                await CreateUserAsync(Mu + "aria@firma.test", confirmed: true);
                typed = Micro + "aria@firma.test";
                break;
        }

        await PendingSignupSeed.IssueAsync(Factory.Services, typed, null, SignupKind.Private);
        var ticket = await NewTicketAsync(typed);
        var confirmed = await InDbAsync(db => db.Users.OrderBy(u => u.Email).Select(u => u.EmailConfirmed).ToListAsync(TestContext.Current.CancellationToken));
        var before = await CountsAsync();

        using var response = await ProviderApi.Finish(Client, ticket, "private");
        using var organization = await ProviderApi.Finish(Client, ticket, "organization", "Firma");

        var bodies = new[] { await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await organization.Content.ReadAsStringAsync(TestContext.Current.CancellationToken) };
        await SignupApi.AssertAlreadyRegisteredAsync(response);
        await SignupApi.AssertAlreadyRegisteredAsync(organization);
        await AssertNoSecretsAnywhereAsync([ticket.Value, ticket.Binding], bodies);
        await AssertNothingChangedAsync(before);   // the ticket stays, and so does the pending sign-up (unlike a completion through the mail)
        Assert.Equal(
            confirmed, await InDbAsync(db => db.Users.OrderBy(u => u.Email).Select(u => u.EmailConfirmed).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, before.Pending);
        Assert.Equal(1, before.Tickets);
        Assert.Equal(0, await LoginsAsync());
    }

    [Fact]
    public async Task An_identity_linked_meanwhile_is_409_already_registered()   // criterion 19, spec 0011 → finish, step 7
    {
        var other = await CreateUserAsync("someone@gmail.test", confirmed: true);
        var ticket = await NewTicketAsync();
        await LinkAsync(other.Id, "google", NewSubject);
        var before = await CountsAsync();

        using var response = await ProviderApi.Finish(Client, ticket, "private");

        await SignupApi.AssertAlreadyRegisteredAsync(response);
        Assert.Equal(0, await AccountsAsync("NEW@GMAIL.TEST"));
        await AssertNothingChangedAsync(before);
    }

    [Theory]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("no_cookie")]
    [InlineData("another_browser")]
    [InlineData("unknown")]
    public async Task A_ticket_that_is_used_expired_unbound_or_unknown_is_400_invalid_ticket_and_makes_nothing(string how)   // spec 0011 → finish, step 3, Review Focus 2
    {
        var ticket = await NewTicketAsync();
        var other = await TicketAsync("google", GooglePerson(email: "other@gmail.test", subject: "500000000000000000006"));
        switch (how)
        {
            case "used":
                using (var first = await ProviderApi.Finish(Client, ticket, "private"))
                {
                    Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                }

                break;
            case "expired":
                Clock.Advance(TimeSpan.FromMinutes(11));
                break;
        }

        var before = await CountsAsync();
        var browser = how switch { "no_cookie" => null, "another_browser" => other.Binding, _ => ticket.Binding };
        var text = how == "unknown" ? new string('A', 43) : ticket.Value;
        using var response = await ProviderApi.PostWithBinding(Client, ProviderApi.FinishPath, new { ticket = text, kind = "private" }, browser);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(before, await CountsAsync());
        Assert.Equal(how == "used" ? 1 : 0, await AccountsAsync("NEW@GMAIL.TEST"));
    }

    [Fact]
    public async Task A_ticket_of_another_purpose_or_without_its_binding_is_invalid_ticket()   // spec 0011 → finish, step 3
    {
        var reauth = await TicketAsync("google", GooglePerson(email: NewPerson, subject: NewSubject), "?purpose=reauth");
        var unbound = await NewTicketAsync();
        var before = await CountsAsync();

        using var one = await ProviderApi.Finish(Client, reauth, "private");
        using var two = await ProviderApi.Finish(Client, unbound with { Binding = reauth.Binding }, "private");

        await TenancyApi.AssertErrorAsync(one, HttpStatusCode.BadRequest, "invalid_ticket");
        await TenancyApi.AssertErrorAsync(two, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task A_ticket_of_another_purpose_is_invalid_ticket_before_its_address_and_the_switches_are_judged()   // spec 0011 → finish, step 3 comes before steps 4 and 5
    {
        var reauth = await TicketAsync("microsoft", WorkMicrosoft(email: "new@firma.test", subject: "00000000-0000-0000-0000-00000000b003"), "?purpose=reauth");
        SetManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.PrivateBlock);

        using var response = await ProviderApi.Finish(Client, reauth, "organization", "Firma");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
    }

    [Fact]
    public async Task A_finish_whose_rows_cannot_be_written_makes_nothing_and_leaves_the_ticket()   // criterion 18: one transaction
    {
        var ticket = await NewTicketAsync();
        var before = await CountsAsync(withAudit: false);
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var response = await ProviderApi.Finish(Client, ticket, "private");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(before, await CountsAsync(withAudit: false));
    }
}
