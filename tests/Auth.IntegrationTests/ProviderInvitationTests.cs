using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0011 → exchange, purpose <c>invite</c> (criteria 21 and 22): the acceptance, every refusal with what it leaves behind, the order of the
/// checks, and the races (the address lock is held in the test, as <c>ProviderFinishRaceTests</c> does, while the world changes).
/// </summary>
public sealed class ProviderInvitationTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Invited = "jan@firma.test";
    private const string Normalized = "JAN@FIRMA.TEST";
    private const string JanSubject = "700000000000000000007";
    private const char Mu = (char)0x03BC;
    private const char Micro = (char)0x00B5;
    private const char FinalSigma = (char)0x03C2;
    private const char Sigma = (char)0x03C3;
    private const char LongS = (char)0x017F;

    private async Task<(Guid Company, Guid Invite, string Token)> InvitationAsync(string address = Invited, Guid? company = null)
    {
        var id = company ?? await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(id, address, "user");
        await EnqueueInvitationAsync(invite, address);
        await DispatchAsync();
        return (id, invite, TokenIn(Mail.Sent.Last(m => m.To == address)));
    }

    private static FakeSignIn Jan(string email = Invited, bool verified = true) => GooglePerson(email: email, verified: verified, subject: JanSubject);

    private Task<ProviderApi.Ticket> InviteTicketAsync(string token, FakeSignIn person, string provider = "google") =>
        TicketAsync(provider, person, "?invite=" + token);

    private Task<int> AccountsAsync() => InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == Normalized, TestContext.Current.CancellationToken));

    private Task<int> LoginsAsync() => InDbAsync(db => db.UserLogins.CountAsync(TestContext.Current.CancellationToken));

    private sealed record Counts(int Accounts, int Companies, int Memberships, int Logins, int Tickets, int Invites, int AuditRows);

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
                await db.Invites.CountAsync(cancellation),
                withAudit ? await db.AuditEvents.CountAsync(cancellation) : -1);
        });

    private async Task AssertInvitationStaysAsync(Guid invite, string token)
    {
        var row = await InviteAsync(invite);
        Assert.NotNull(row);
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);
    }

    /// <summary>A refusal changes nothing: the rows counted before (the ticket, the invitation and the audit rows too) are the rows now.</summary>
    private async Task AssertRefusedAsync(Counts before, Guid invite, string token)
    {
        Assert.Equal(before, await CountsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.InviteAccepted));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
        await AssertInvitationStaysAsync(invite, token);
    }

    /// <summary>The ticket, its binding and the invitation token appear in no line the exchange logged and in no body given.</summary>
    private void AssertNoSecretsInLogs(int mark, string[] secrets, params string[] bodies)
    {
        var lines = string.Join('\n', Logs.Entries.Skip(mark).Select(e => e.Message));
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, lines, StringComparison.Ordinal);
            Assert.All(bodies, body => Assert.DoesNotContain(secret, body, StringComparison.Ordinal));
        }
    }

    /// <summary>Sends the exchange while the address lock is held, waits until it is queued behind it, runs <paramref name="change"/>, releases.</summary>
    private async Task<HttpResponseMessage> ExchangeWhileLockedAsync(ProviderApi.Ticket ticket, Func<Task> change)
    {
        await using var held = await HeldLock.AddressAsync(Factory, Normalized);
        var exchange = ProviderApi.Exchange(Client, ticket);
        await HeldLock.WaitForWaitersAsync(Factory, 1);
        await change();
        await held.ReleaseAsync();
        return await exchange;
    }

    [Fact]
    public async Task An_invitation_through_google_makes_the_account_without_a_password_and_signs_in_to_the_company()   // criterion 21
    {
        var (company, invite, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());
        var cancellation = TestContext.Current.CancellationToken;
        var mark = Logs.Entries.Count;

        using var response = await ProviderApi.Exchange(Client, ticket);

        var raw = await response.Content.ReadAsStringAsync(cancellation);
        var session = await SessionApi.ReadSessionAsync(response);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal("organization", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["user"], AccessTokens.Array(session.AccessToken, "roles"));
        var account = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "JAN@FIRMA.TEST", cancellation));
        Assert.Equal(account.Id.ToString(), AccessTokens.Text(session.AccessToken, "sub"));
        Assert.Null(account.PasswordHash);
        Assert.True(account.EmailConfirmed);
        Assert.Equal(Invited, account.Email);
        var login = Assert.Single(await InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(cancellation)));
        Assert.Equal(("google", JanSubject, account.Id), (login.LoginProvider, login.ProviderKey, login.UserId));
        var member = await InDbAsync(db => db.Memberships.AsNoTracking().SingleAsync(m => m.UserId == account.Id, cancellation));
        Assert.Equal(company, member.CompanyId);
        Assert.Null(await InviteAsync(invite));
        Assert.Equal(0, await TicketCountAsync());
        var accepted = await SingleAsync(AuditKinds.InviteAccepted);
        Assert.Equal(["provider", "role"], AuditApi.DetailNames(accepted));
        Assert.Equal("user", AuditApi.Text(accepted, "role"));
        Assert.Equal("google", AuditApi.Text(accepted, "provider"));
        Assert.Equal(account.Id, accepted.SubjectUserId);
        Assert.Null(accepted.ActorUserId);   // the person who holds the link is anonymous
        Assert.Equal(company, accepted.OrgId);
        Assert.Equal("Acme", accepted.OrgName);
        Assert.Equal(invite, accepted.TargetId);
        var signedIn = await SingleAsync(AuditKinds.LoginSucceeded);
        Assert.Equal("google", AuditApi.Text(signedIn, "provider"));
        Assert.Equal(account.Id, signedIn.SubjectUserId);
        Assert.Equal(company, signedIn.OrgId);
        _ = await SessionApi.RefreshOk(Client, session.RefreshToken);   // the cookie refreshes like a login's
        using var password = await LoginApi.Login(Client, Invited, "Any-Passw0rd-1");
        Assert.Equal(HttpStatusCode.Unauthorized, password.StatusCode);   // no password: the 401 of an unknown address
        await AssertNoSecretsAsync(ticket.Value, ticket.Binding, token, session.AccessToken, session.RefreshToken);
        AssertNoSecretsInLogs(mark, [token], raw);
        var text = Logs.Text;
        Assert.All([ticket.Value, ticket.Binding, session.AccessToken, session.RefreshToken], secret => Assert.DoesNotContain(secret, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_used_ticket_is_invalid_ticket_and_the_identity_then_signs_in_as_a_linked_one()   // criterion 21: the ticket and the invitation are used up; the identity is the person
    {
        var (_, _, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());
        using (var first = await ProviderApi.Exchange(Client, ticket))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var again = await ProviderApi.Exchange(Client, ticket);
        var later = await TicketAsync("google", Jan());   // an ordinary sign-in now: the identity is a login of the new account
        using var linked = await ProviderApi.Exchange(Client, later);

        await TenancyApi.AssertErrorAsync(again, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal(1, await AccountsAsync());
        Assert.Equal(1, await LoginsAsync());
    }

    [Fact]
    public async Task An_invitation_through_microsoft_names_microsoft_in_its_login_and_its_audit_rows()   // criterion 21: the provider of the ticket, not a fixed one
    {
        var (_, _, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, PersonalMicrosoft(email: Invited), "microsoft");

        using var response = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("microsoft", Assert.Single(await InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken))).LoginProvider);
        Assert.Equal("microsoft", AuditApi.Text(await SingleAsync(AuditKinds.InviteAccepted), "provider"));
        Assert.Equal("microsoft", AuditApi.Text(await SingleAsync(AuditKinds.LoginSucceeded), "provider"));
    }

    [Fact]
    public async Task The_invited_address_in_another_ASCII_case_is_accepted_and_the_account_keeps_the_invited_spelling()   // criterion 21: IsSameAddress folds ASCII case only
    {
        var (_, _, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan(email: "JAN@Firma.Test"));

        using var response = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Invited, (await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == Normalized, TestContext.Current.CancellationToken))).Email);
    }

    [Theory]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("cancelled")]
    [InlineData("replaced")]
    [InlineData("unknown")]
    public async Task A_used_expired_cancelled_or_replaced_invitation_is_400_invalid_token(string how)   // criterion 22
    {
        var (_, invite, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(how == "unknown" ? new string('A', 43) : token, Jan());   // the start reads no invitation: any well-formed token makes a ticket
        var cancellation = TestContext.Current.CancellationToken;
        switch (how)
        {
            case "used":
                using (var accepted = await TenancyApi.Accept(Client, token, "Invited-Passw0rd"))
                {
                    Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
                }

                break;
            case "expired":
                var past = StorableTime.Now(Clock).AddSeconds(-1);
                await InDbAsync(db => db.Invites.Where(i => i.Id == invite).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, past), cancellation));
                break;
            case "cancelled":
                await InDbAsync(db => db.Invites.Where(i => i.Id == invite).ExecuteDeleteAsync(cancellation));
                break;
            case "replaced":
                await InDbAsync(async db => (await InviteTokens.IssueAsync(db, invite, StorableTime.Now(Clock), cancellation)).Length);
                break;
        }

        var before = await CountsAsync();
        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_token");
        Assert.False(response.Headers.Contains("Set-Cookie"));   // auth_pt is not cleared
        Assert.Equal(before, await CountsAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Theory]
    [InlineData("another")]
    [InlineData("unverified")]
    [InlineData("none")]
    [InlineData("work")]
    public async Task Another_address_an_unverified_one_or_none_is_403_address_mismatch_and_the_invitation_stays(string which)   // criterion 22, Review Focus 5
    {
        var (_, invite, token) = await InvitationAsync();
        var provider = which == "work" ? "microsoft" : "google";
        var person = which switch
        {
            "another" => Jan(email: "other@firma.test"),
            "unverified" => Jan(verified: false),
            "work" => WorkMicrosoft(email: Invited),   // a work tenant hands out any address, unverified
            _ => GooglePerson(subject: JanSubject, defect: FakeDefects.NoEmail),
        };
        var ticket = await InviteTicketAsync(token, person, provider);
        var before = await CountsAsync();
        var mark = Logs.Entries.Count;

        using var response = await ProviderApi.Exchange(Client, ticket);

        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "address_mismatch");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await AssertRefusedAsync(before, invite, token);
        AssertNoSecretsInLogs(mark, [token, ticket.Value, ticket.Binding], raw);
        using var accepted = await TenancyApi.Accept(Client, token, "Invited-Passw0rd");   // it still works
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    public static TheoryData<string, string> LookAlikes => new()
    {
        { Mu + "aria@firma.test", Micro + "aria@firma.test" },
        { Micro + "aria@firma.test", Mu + "aria@firma.test" },
        { "kosta" + FinalSigma + "@firma.test", "kosta" + Sigma + "@firma.test" },
    };

    [Theory]
    [MemberData(nameof(LookAlikes))]
    public async Task A_look_alike_spelling_of_the_invited_address_is_403_address_mismatch_and_the_invitation_stays(string invited, string typed)   // criterion 22: IsSameAddress, not the normaliser
    {
        var (_, invite, token) = await InvitationAsync(invited);
        var ticket = await InviteTicketAsync(token, Jan(email: typed));
        var before = await CountsAsync();

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "address_mismatch");
        await AssertRefusedAsync(before, invite, token);
    }

    [Theory]
    [MemberData(nameof(LookAlikes))]
    public async Task An_account_under_a_look_alike_of_the_invited_address_is_400_invalid_token_and_is_not_touched(string account, string invited)   // as the password acceptance: the link proves its own mailbox only
    {
        var (_, invite, token) = await InvitationAsync(invited);
        var owner = await CreateUserAsync(account, confirmed: true, member: false);
        var ticket = await InviteTicketAsync(token, Jan(email: invited));
        var before = await CountsAsync();

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_token");
        await AssertRefusedAsync(before, invite, token);
        var kept = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == owner.Id, TestContext.Current.CancellationToken));
        Assert.Equal(account, kept.Email);   // the account keeps its address and its password
        Assert.NotNull(kept.PasswordHash);
    }

    [Theory]
    [MemberData(nameof(LookAlikes))]
    public async Task A_mismatching_address_is_403_before_an_account_under_a_look_alike_is_judged(string account, string invited)   // criterion 22: step 2 comes before the look-alike account's invalid_token
    {
        var (_, invite, token) = await InvitationAsync(invited);
        await CreateUserAsync(account, confirmed: true, member: false);
        var another = await InviteTicketAsync(token, Jan(email: "other@firma.test"));
        var unverified = await InviteTicketAsync(token, Jan(email: invited, verified: false));
        var before = await CountsAsync();

        using var one = await ProviderApi.Exchange(Client, another);
        using var two = await ProviderApi.Exchange(Client, unverified);

        await TenancyApi.AssertErrorAsync(one, HttpStatusCode.Forbidden, "address_mismatch");
        await TenancyApi.AssertErrorAsync(two, HttpStatusCode.Forbidden, "address_mismatch");   // nothing said about the account under the look-alike
        await AssertRefusedAsync(before, invite, token);
    }

    [Fact]
    public async Task An_invitation_stored_for_an_address_that_may_stand_for_another_is_400_invalid_token_before_the_address_is_judged()   // criterion 22: step 1 comes before step 2
    {
        var address = LongS + "teve@firma.test";   // a long s: upper-cases to S
        var (_, invite, token) = await InvitationAsync(address);
        var ticket = await InviteTicketAsync(token, Jan(email: address));   // the identity rules make such an address unverified: step 2 would say address_mismatch
        var before = await CountsAsync();

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_token");
        await AssertRefusedAsync(before, invite, token);
    }

    [Fact]
    public async Task An_unusable_invitation_is_400_invalid_token_before_the_address_is_judged()   // criterion 22: step 1 comes before step 2
    {
        var (_, invite, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan(email: "other@firma.test"));
        await InDbAsync(db => db.Invites.Where(i => i.Id == invite).ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_token");
    }

    [Theory]
    [InlineData("member")]
    [InlineData("unconfirmed")]
    [InlineData("no_space")]
    public async Task A_company_members_address_is_409_already_member_and_the_invitation_stays(string which)   // criterion 22; an account with no space is refused the same way
    {
        var (_, invite, token) = await InvitationAsync();
        var existing = await CreateUserAsync(Invited, confirmed: which != "unconfirmed", member: which != "no_space");   // a member of the development company, or of none
        var ticket = await InviteTicketAsync(token, Jan());
        var before = await CountsAsync();
        var hash = (await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == existing.Id, TestContext.Current.CancellationToken))).PasswordHash;

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "already_member");
        await AssertRefusedAsync(before, invite, token);
        var after = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == existing.Id, TestContext.Current.CancellationToken));
        Assert.Equal((hash, which != "unconfirmed"), (after.PasswordHash, after.EmailConfirmed));   // not taken over: password kept, email not confirmed by a provider
        Assert.Equal(0, await LoginsAsync());
    }

    [Fact]
    public async Task A_private_accounts_address_is_409_private_account_exists_and_the_invitation_stays()   // criterion 22, Settled 15
    {
        var (_, invite, token) = await InvitationAsync();
        await CreatePrivateAccountAsync(Invited);
        var ticket = await InviteTicketAsync(token, Jan());
        var before = await CountsAsync();

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "private_account_exists");
        await AssertRefusedAsync(before, invite, token);
        using var moved = await TenancyApi.AcceptLeaving(Client, token);   // the move of spec 0010 still works
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
    }

    [Fact]
    public async Task An_identity_already_linked_is_409_provider_already_linked_and_the_invitation_stays()   // criterion 22
    {
        var (_, invite, token) = await InvitationAsync();
        var other = await CreateUserAsync("jan.old@firma.test", confirmed: true);
        await LinkAsync(other.Id, "google", JanSubject);
        var ticket = await InviteTicketAsync(token, Jan());
        var before = await CountsAsync();

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "provider_already_linked");
        await AssertRefusedAsync(before, invite, token);
        Assert.Equal(0, await AccountsAsync());
    }

    [Theory]
    [InlineData("member", "unverified")]
    [InlineData("private", "unverified")]
    [InlineData("linked", "unverified")]
    [InlineData("member", "another")]
    [InlineData("linked", "another")]
    public async Task A_mismatching_address_is_403_before_the_account_and_the_identity_are_judged(string existing, string address)   // criterion 22: step 2 comes before step 3
    {
        var (_, invite, token) = await InvitationAsync();
        switch (existing)
        {
            case "member":
                await CreateUserAsync(Invited, confirmed: true);
                break;
            case "private":
                await CreatePrivateAccountAsync(Invited);
                break;
            default:
                await LinkAsync((await CreateUserAsync("jan.old@firma.test", confirmed: true)).Id, "google", JanSubject);
                break;
        }

        var ticket = await InviteTicketAsync(token, address == "another" ? Jan(email: "other@firma.test") : Jan(verified: false));
        var before = await CountsAsync();

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "address_mismatch");
        await AssertRefusedAsync(before, invite, token);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("private")]
    public async Task An_address_with_an_account_is_409_before_the_identity_is_judged(string kind)   // criterion 22: the account's answer comes before provider_already_linked
    {
        var (_, invite, token) = await InvitationAsync();
        if (kind == "member")
        {
            await CreateUserAsync(Invited, confirmed: true);
        }
        else
        {
            await CreatePrivateAccountAsync(Invited);
        }

        await LinkAsync((await CreateUserAsync("jan.old@firma.test", confirmed: true)).Id, "google", JanSubject);
        var ticket = await InviteTicketAsync(token, Jan());
        var before = await CountsAsync();

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, kind == "member" ? "already_member" : "private_account_exists");
        await AssertRefusedAsync(before, invite, token);
    }

    [Fact]
    public async Task An_invitation_ticket_is_not_good_at_the_link_or_the_finish()   // spec 0011: the link and the finish take sign-in tickets only
    {
        var (_, invite, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());
        var before = await CountsAsync();

        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);
        using var finished = await ProviderApi.Finish(Client, ticket, "private");

        await TenancyApi.AssertErrorAsync(linked, HttpStatusCode.BadRequest, "invalid_ticket");
        await TenancyApi.AssertErrorAsync(finished, HttpStatusCode.BadRequest, "invalid_ticket");
        await AssertRefusedAsync(before, invite, token);
    }

    [Theory]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("no_cookie")]
    [InlineData("another_browser")]
    [InlineData("unknown")]
    [InlineData("blank")]
    [InlineData("reauth")]
    public async Task A_ticket_that_is_used_expired_unbound_unknown_blank_or_of_reauth_is_400_invalid_ticket_and_the_invitation_stays(string how)   // spec 0011 → exchange, step 2, Review Focus 2
    {
        var (_, invite, token) = await InvitationAsync();
        var ticket = how == "reauth" ? await TicketAsync("google", Jan(), "?purpose=reauth") : await InviteTicketAsync(token, Jan());
        var other = await TicketAsync("google", GooglePerson(email: "other@firma.test"));
        if (how == "used")
        {
            using var first = await ProviderApi.Exchange(Client, ticket);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        else if (how == "expired")
        {
            Clock.Advance(TimeSpan.FromMinutes(11));
        }

        var before = await CountsAsync();
        var binding = how switch { "no_cookie" => null, "another_browser" => other.Binding, _ => ticket.Binding };
        var text = how switch { "unknown" => new string('A', 43), "blank" => "", _ => ticket.Value };
        using var response = await ProviderApi.Exchange(Client, text, binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(before, await CountsAsync());
        Assert.Equal(how == "used" ? 1 : 0, await AccountsAsync());   // only the first, good exchange of "used" made the account
        if (how != "used")
        {
            await AssertInvitationStaysAsync(invite, token);
        }
    }

    // Triggers that make the database refuse one write after the acceptance has done its own work: the sign-in then fails as a whole.
    private const string RefuseLoginSucceeded =
        "CREATE FUNCTION refuse_row() RETURNS trigger AS $$ BEGIN IF TG_TABLE_NAME <> 'audit_events' OR NEW.kind = 'login.succeeded' THEN RAISE EXCEPTION 'refused'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql";

    private const string RefuseAudit = "CREATE TRIGGER refuse_row BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION refuse_row()";
    private const string RefuseTokens = """CREATE TRIGGER refuse_row BEFORE INSERT ON "OpenIddictTokens" FOR EACH ROW EXECUTE FUNCTION refuse_row()""";
    private const string AllowAudit = "DROP TRIGGER refuse_row ON audit_events";
    private const string AllowTokens = """DROP TRIGGER refuse_row ON "OpenIddictTokens" """;

    [Theory]
    [InlineData("audit")]
    [InlineData("tokens")]
    public async Task A_sign_in_that_fails_after_the_account_was_made_rolls_the_whole_acceptance_back(string which)   // criterion 21: one transaction, the sign-in included
    {
        var (_, invite, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());
        var before = await CountsAsync();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseLoginSucceeded, TestContext.Current.CancellationToken));
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(which == "audit" ? RefuseAudit : RefuseTokens, TestContext.Current.CancellationToken));

        using var response = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("""{"error":"internal_error"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(response.Headers.Contains("Set-Cookie"));   // no refresh cookie, auth_pt not cleared
        Assert.Equal(0, await AccountsAsync());
        await AssertRefusedAsync(before, invite, token);   // no account, login, membership; the invitation, the ticket and the audit rows as they were (invite.accepted too)
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(which == "audit" ? AllowAudit : AllowTokens, TestContext.Current.CancellationToken));
        using var again = await ProviderApi.Exchange(Client, ticket);   // both are usable still
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task An_acceptance_whose_rows_cannot_be_written_makes_nothing_and_leaves_the_invitation_and_the_ticket()   // criterion 21: one transaction
    {
        var (_, invite, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());
        var before = await CountsAsync(withAudit: false);
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var response = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(before, await CountsAsync(withAudit: false));
        await AssertInvitationStaysAsync(invite, token);
    }

    [Fact]
    public async Task Two_exchanges_of_one_invitation_ticket_make_one_account_one_session_and_one_invite_accepted()   // spec 0011 → exchange: parallel requests with one ticket, one gets it
    {
        var (_, _, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());

        Task<HttpResponseMessage> one, two;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            one = ProviderApi.Exchange(Client, ticket);
            await HeldLock.WaitForWaitersAsync(Factory, 1);   // the company locked and the invitation used up, waiting for the address
            two = ProviderApi.Exchange(Client, ticket);
            await HeldLock.WaitForWaitersAsync(Factory, 2);   // waiting for the company
            await held.ReleaseAsync();
        }

        using var first = await one;
        using var second = await two;
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await TenancyApi.AssertErrorAsync(second, HttpStatusCode.BadRequest, "invalid_token");   // the invitation is gone
        Assert.Equal(1, await AccountsAsync());
        Assert.Equal(1, await LoginsAsync());
        Assert.Equal(0, await TicketCountAsync());
        Assert.Single(await AuditAsync(AuditKinds.InviteAccepted));
        Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task Two_tickets_of_two_identities_for_one_invitation_make_one_account_and_the_loser_keeps_its_ticket()   // spec 0011 → exchange, invite step 1: the invitation is used once
    {
        var (_, _, token) = await InvitationAsync();
        var google = await InviteTicketAsync(token, Jan());
        var microsoft = await InviteTicketAsync(token, PersonalMicrosoft(email: Invited), "microsoft");

        Task<HttpResponseMessage> one, two;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            one = ProviderApi.Exchange(Client, google);
            await HeldLock.WaitForWaitersAsync(Factory, 1);
            two = ProviderApi.Exchange(Client, microsoft);
            await HeldLock.WaitForWaitersAsync(Factory, 2);
            await held.ReleaseAsync();
        }

        using var made = await one;
        using var refused = await two;
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.BadRequest, "invalid_token");
        Assert.Equal("google", Assert.Single(await InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken))).LoginProvider);
        Assert.Equal(1, await TicketCountAsync());   // the refused ticket stays until it expires
        Assert.Single(await AuditAsync(AuditKinds.InviteAccepted));
    }

    [Fact]
    public async Task An_invitation_accepted_with_a_password_while_the_exchange_waits_makes_the_exchange_400_invalid_token_and_its_ticket_stays()   // spec 0011 → exchange: used meanwhile
    {
        var (_, _, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());

        Task<HttpResponseMessage> acceptance, exchange;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            acceptance = TenancyApi.Accept(Client, token, "Invited-Passw0rd");
            await HeldLock.WaitForWaitersAsync(Factory, 1);   // the invitation used up, waiting for the address
            exchange = ProviderApi.Exchange(Client, ticket);
            await HeldLock.WaitForWaitersAsync(Factory, 2);   // waiting for the company
            await held.ReleaseAsync();
        }

        using var accepted = await acceptance;
        using var refused = await exchange;
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.BadRequest, "invalid_token");
        Assert.Equal(1, await AccountsAsync());
        Assert.Equal(0, await LoginsAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task The_exchange_holds_the_company_lock_while_it_waits_for_the_address_so_another_change_of_the_company_waits_for_it()   // spec 0010 → Locks: the company first, then the address
    {
        var (company, _, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var (_, _, token) = await InvitationAsync(company: company);
        var other = await AddInviteAsync(company, "other@firma.test", "user");
        var ticket = await InviteTicketAsync(token, Jan());

        Task<HttpResponseMessage> exchange, cancelling;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            exchange = ProviderApi.Exchange(Client, ticket);
            await HeldLock.WaitForWaitersAsync(Factory, 1);   // the company locked, waiting for the address
            cancelling = TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/invites/{other}", adminToken);
            await HeldLock.WaitForWaitersAsync(Factory, 2);   // another invitation of the company cannot be cancelled meanwhile: it waits for the company
            Assert.NotNull(await InviteAsync(other));
            await held.ReleaseAsync();
        }

        using var made = await exchange;
        using var cancelled = await cancelling;
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Null(await InviteAsync(other));
    }

    [Fact]
    public async Task An_invitation_cancelled_while_the_exchange_waits_makes_it_400_invalid_token_and_its_ticket_stays()   // spec 0011 → exchange: revoked meanwhile
    {
        var (company, adminId, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        Assert.NotEqual(Guid.Empty, adminId);
        var (_, invite, token) = await InvitationAsync(company: company);
        var ticket = await InviteTicketAsync(token, Jan());

        Task<HttpResponseMessage> cancelling, exchange;
        await using (var held = await HeldLock.AuditTableAsync(Factory))
        {
            cancelling = TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/invites/{invite}", adminToken);
            await HeldLock.WaitForWaitersAsync(Factory, 1);   // the invitation deleted, not committed: the cancel waits at its audit row
            exchange = ProviderApi.Exchange(Client, ticket);
            await HeldLock.WaitForWaitersAsync(Factory, 2);   // waiting for the company
            await held.ReleaseAsync();
        }

        using var cancelled = await cancelling;
        using var refused = await exchange;
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.BadRequest, "invalid_token");
        Assert.Equal(0, await AccountsAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.InviteAccepted));
    }

    [Fact]
    public async Task A_sign_up_completion_ahead_of_the_exchange_makes_the_account_and_the_exchange_is_409_already_member_with_the_invitation_kept()   // spec 0011 → exchange: account created meanwhile; criterion 20
    {
        var (_, invite, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());
        var signupToken = await PendingSignupSeed.IssueAsync(Factory.Services, Invited, "Firma");

        Task<HttpResponseMessage> completion, exchange;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            completion = SignupApi.Complete(Client, signupToken, Password, "Firma");
            await HeldLock.WaitForWaitersAsync(Factory, 1);
            exchange = ProviderApi.Exchange(Client, ticket);
            await HeldLock.WaitForWaitersAsync(Factory, 2);
            await held.ReleaseAsync();
        }

        using var completed = await completion;
        using var refused = await exchange;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.Conflict, "already_member");
        Assert.Equal(1, await AccountsAsync());
        Assert.Equal(0, await LoginsAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.InviteAccepted));
        await AssertInvitationStaysAsync(invite, token);
    }

    [Theory]
    [InlineData("company")]
    [InlineData("private")]
    public async Task An_account_made_while_the_exchange_waits_for_the_address_is_409_and_the_invitation_and_ticket_stay(string kind)   // spec 0011 → exchange: account created meanwhile
    {
        var (_, invite, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());

        using var response = await ExchangeWhileLockedAsync(
            ticket, () => kind == "company" ? CreateUserAsync(Invited, confirmed: true) : CreatePrivateAccountAsync(Invited));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, kind == "company" ? "already_member" : "private_account_exists");
        Assert.Equal(1, await AccountsAsync());
        Assert.Equal(0, await LoginsAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.InviteAccepted));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
        await AssertInvitationStaysAsync(invite, token);
    }

    [Fact]
    public async Task A_ticket_that_expired_while_the_exchange_waited_for_the_address_lock_is_400_invalid_ticket_and_makes_nothing()   // spec 0011 → exchange, step 2: the clock is read after the locks
    {
        var (_, invite, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());

        using var response = await ExchangeWhileLockedAsync(ticket, () => { Clock.Advance(TimeSpan.FromMinutes(11)); return Task.CompletedTask; });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, await AccountsAsync());
        Assert.Equal(0, await LoginsAsync());
        Assert.Equal(1, await TicketCountAsync());   // expired, not yet pruned
        Assert.Empty(await AuditAsync(AuditKinds.InviteAccepted));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
        await AssertInvitationStaysAsync(invite, token);   // the use of the invitation was rolled back with the rest
    }

    [Fact]
    public async Task An_acceptance_through_a_provider_ends_no_session_and_leaves_the_login_streak_of_the_address_alone()   // spec 0011 → Limits: unlike the acceptance with a password
    {
        var (_, _, token) = await InvitationAsync();
        var other = await CreateUserAsync("someone@firma.test", confirmed: true);
        var session = await SessionApi.LoginAsync(Client, "someone@firma.test", UserPassword);
        await LockoutApi.FailAsync(Client, Clock, Invited, 3);   // an address without an account counts too
        var ticket = await InviteTicketAsync(token, Jan());

        using var response = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var attempts = await InDbAsync(db => db.LoginStreaks.Where(s => s.IdentifierHash == LoginIdentifier.HashOf(Normalized))
            .Select(s => s.AttemptCount).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, attempts);
        _ = await SessionApi.RefreshOk(Client, session.RefreshToken);   // another account's session is as it was
        Assert.NotEqual(other.Id.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(response)).AccessToken, "sub"));
    }

    [Fact]
    public async Task An_identity_linked_while_the_exchange_waits_for_the_address_is_409_provider_already_linked_and_the_invitation_stays()   // spec 0011 → exchange, invite step 3
    {
        var (_, invite, token) = await InvitationAsync();
        var other = await CreateUserAsync("jan.old@firma.test", confirmed: true);
        var ticket = await InviteTicketAsync(token, Jan());

        using var response = await ExchangeWhileLockedAsync(ticket, () => LinkAsync(other.Id, "google", JanSubject));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "provider_already_linked");
        Assert.Equal(0, await AccountsAsync());
        Assert.Equal(1, await LoginsAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.InviteAccepted));
        await AssertInvitationStaysAsync(invite, token);
    }

    [Fact]
    public async Task A_ticket_used_up_while_the_exchange_waits_for_the_address_is_400_invalid_ticket_and_the_invitation_stays()   // spec 0011 → exchange: the ticket is used up in the transaction of the acceptance
    {
        var (_, invite, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());

        using var response = await ExchangeWhileLockedAsync(
            ticket, () => InDbAsync(db => db.ProviderTickets.ExecuteDeleteAsync(TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, await AccountsAsync());
        Assert.Equal(0, await LoginsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.InviteAccepted));
        await AssertInvitationStaysAsync(invite, token);   // the use of the invitation was rolled back with the rest
    }

    [Fact]
    public async Task An_account_that_a_removal_deletes_between_the_read_of_it_and_the_read_of_its_space_is_made_anew()   // spec 0011 → exchange: removed meanwhile; as spec 0010's acceptance
    {
        var (acme, _, _) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var worker = await AddMemberAsync(acme, Invited, "user");
        var (_, invite, token) = await InvitationAsync(company: await CreateCompanyAsync("Globex"));
        var ticket = await InviteTicketAsync(token, Jan());

        Task<HttpResponseMessage> exchange;
        await using (var held = await HeldLock.MembershipsAsync(Factory))
        {
            exchange = ProviderApi.Exchange(Client, ticket);
            await HeldLock.WaitForWaitersAsync(Factory, 1);   // the account is read; the read of its space waits for the memberships
            await held.DeleteAccountAsync(worker);            // the removal's effect: the account and its membership go, and commit with the release
            await held.ReleaseAsync();
        }

        using var response = await exchange;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // never a 500: the address belongs to no account now
        Assert.Null(await InviteAsync(invite));
        var account = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == Normalized, TestContext.Current.CancellationToken));
        Assert.NotEqual(worker, account.Id);
        Assert.Null(account.PasswordHash);
        Assert.Equal(1, await LoginsAsync());
    }
}
