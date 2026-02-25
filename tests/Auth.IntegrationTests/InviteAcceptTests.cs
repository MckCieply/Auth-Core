using System.Net;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class InviteAcceptTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Address = "worker@acme.test";
    private const string Password = "Chosen-Passw0rd";
    private const string InvalidToken = """{"error":"invalid_token"}""";

    /// <summary>An invitation of the company, with its mail composed: returns the invitation and the token as the mail holds it.</summary>
    private async Task<(Guid Invite, string Token)> MailedAsync(Guid company, string address = Address, string role = "user")
    {
        var invite = await AddInviteAsync(company, address, role);
        await EnqueueInvitationAsync(invite, address);
        await DispatchAsync();
        return (invite, TokenIn(Mail.Sent.Last(m => m.To == address)));
    }

    private async Task<ApplicationUser?> UserAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
    }

    private Task<List<Membership>> MembershipsAsync() =>
        InDbAsync(db => db.Memberships.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    private static async Task AssertInvalidTokenAsync(HttpResponseMessage response)
    {
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_token");
        Assert.Equal(InvalidToken, await response.Content.ReadAsStringAsync());
    }

    // ---- the whole way

    [Fact]
    public async Task A_manager_invites_the_person_accepts_and_logs_in_with_the_company_and_the_role()   // criteria 2, 3, 4
    {
        var company = await CreateCompanyAsync("Acme");
        await AddMemberAsync(company, "boss@acme.test", "admin");
        var boss = await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword);
        using (var sent = await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", boss.AccessToken, new { email = Address, role_id = await RoleIdAsync(company, "user") }))
        {
            await TenancyApi.AssertEmptyAsync(sent, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var token = TokenIn(Assert.Single(Mail.Sent));

        using (var preview = await TenancyApi.Preview(Client, token))
        {
            var body = await TenancyApi.ReadOkAsync(preview);
            Assert.Equal(["email", "org_name", "role"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal(Address, body.GetProperty("email").GetString());
            Assert.Equal("Acme", body.GetProperty("org_name").GetString());
            Assert.Equal("user", body.GetProperty("role").GetString());
        }

        using (var accepted = await TenancyApi.Accept(Client, token, Password))
        {
            await TenancyApi.AssertEmptyAsync(accepted, HttpStatusCode.NoContent);
        }

        var session = await SessionApi.LoginAsync(Client, Address, Password);
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["user"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(["reports:approve", "reports:read"], AccessTokens.Array(session.AccessToken, "permissions"));
    }

    // ---- preview

    [Fact]
    public async Task Preview_leaves_the_token_usable()   // criterion 3
    {
        var company = await CreateCompanyAsync("Acme");
        var (invite, token) = await MailedAsync(company);

        for (var i = 0; i < 3; i++)
        {
            using var preview = await TenancyApi.Preview(Client, token);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        }

        Assert.NotNull(await InviteAsync(invite));
        using var accepted = await TenancyApi.Accept(Client, token, Password);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    [Fact]
    public async Task Preview_shows_the_names_as_they_are_now()
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);
        await InDbAsync(db => db.Companies.Where(c => c.Id == company).ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Acme Holdings"), TestContext.Current.CancellationToken));

        using var preview = await TenancyApi.Preview(Client, token);

        Assert.Equal("Acme Holdings", (await TenancyApi.ReadOkAsync(preview)).GetProperty("org_name").GetString());
    }

    [Fact]
    public async Task Preview_and_accept_of_a_token_that_is_not_usable_give_one_answer_whatever_the_reason()   // criterion 6
    {
        var company = await CreateCompanyAsync("Acme");

        // Used.
        var (_, used) = await MailedAsync(company, "used@acme.test");
        using (var first = await TenancyApi.Accept(Client, used, Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        // Cancelled.
        var (cancelled, cancelledToken) = await MailedAsync(company, "cancelled@acme.test");
        await InDbAsync(db => db.Invites.Where(i => i.Id == cancelled).ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        // Replaced by a resend.
        var (replacedInvite, replaced) = await MailedAsync(company, "replaced@acme.test");
        await EnqueueInvitationAsync(replacedInvite, "replaced@acme.test");
        await DispatchAsync();

        // Expired: a week and a second old.
        var (_, expired) = await MailedAsync(company, "expired@acme.test");
        Clock.Advance(InviteTokens.Lifetime);

        var tokens = new[] { used, cancelledToken, replaced, expired, "unknown-token", new string('x', 43), "Zażółć" };
        var previews = new List<string>();
        var accepts = new List<string>();
        foreach (var token in tokens)
        {
            using var preview = await TenancyApi.Preview(Client, token);
            await AssertInvalidTokenAsync(preview);
            previews.Add(await preview.Content.ReadAsStringAsync());
            using var accept = await TenancyApi.Accept(Client, token, Password);
            await AssertInvalidTokenAsync(accept);
            accepts.Add(await accept.Content.ReadAsStringAsync());
        }

        Assert.Single(previews.Distinct());
        Assert.Single(accepts.Distinct());
        Assert.Equal(previews[0], accepts[0]);
    }

    [Fact]
    public async Task Token_is_good_until_the_seven_days_are_over()   // the 7-day lifetime, with the controlled clock
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        Clock.Advance(InviteTokens.Lifetime - TimeSpan.FromSeconds(1));
        using (var almost = await TenancyApi.Preview(Client, token))
        {
            Assert.Equal(HttpStatusCode.OK, almost.StatusCode);
        }

        Clock.Advance(TimeSpan.FromSeconds(1));
        using var over = await TenancyApi.Preview(Client, token);
        await AssertInvalidTokenAsync(over);
        using var accept = await TenancyApi.Accept(Client, token, Password);
        await AssertInvalidTokenAsync(accept);
        Assert.Null(await UserAsync(Address));
        Assert.Empty(await NonSeedMembershipsAsync());
    }

    /// <summary>The memberships that are not the seed user's in the development company.</summary>
    private async Task<List<Membership>> NonSeedMembershipsAsync()
    {
        var dev = await DevCompanyIdAsync();
        return [.. (await MembershipsAsync()).Where(m => m.CompanyId != dev)];
    }

    [Fact]
    public async Task A_resent_invitation_works_through_the_new_link_only()   // criterion 10
    {
        var company = await CreateCompanyAsync("Acme");
        var (invite, first) = await MailedAsync(company);
        await EnqueueInvitationAsync(invite, Address);
        await DispatchAsync();
        var second = TokenIn(Mail.Sent[1]);

        using (var old = await TenancyApi.Preview(Client, first))
        {
            await AssertInvalidTokenAsync(old);
        }

        using var fresh = await TenancyApi.Preview(Client, second);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task A_cancelled_invitation_cannot_be_previewed_or_accepted_and_is_not_listed()   // criterion 10
    {
        var company = await CreateCompanyAsync("Acme");
        await AddMemberAsync(company, "boss@acme.test", "admin");
        var boss = (await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword)).AccessToken;
        var (invite, token) = await MailedAsync(company);

        using (var cancel = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/invites/{invite}", boss))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }

        using (var preview = await TenancyApi.Preview(Client, token))
        {
            await AssertInvalidTokenAsync(preview);
        }

        using var accept = await TenancyApi.Accept(Client, token, Password);
        await AssertInvalidTokenAsync(accept);
        using var list = await TenancyApi.Get(Client, "/auth/org/invites", boss);
        Assert.Empty((await TenancyApi.ReadOkAsync(list)).GetProperty("invites").EnumerateArray());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"token":42}""")]
    [InlineData("""{"token":null}""")]
    [InlineData("""{"token":"   "}""")]
    [InlineData("""["token"]""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task Malformed_requests_are_a_400_invalid_request(string body)
    {
        foreach (var path in new[] { TenancyApi.PreviewPath, TenancyApi.AcceptPath })
        {
            using var response = await AccountApi.PostRaw(Client, path, body);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }
    }

    [Fact]
    public async Task Accept_without_a_password_or_with_a_nul_in_it_is_a_400_invalid_request()
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        using var missing = await AccountApi.PostRaw(Client, TenancyApi.AcceptPath, $$"""{"token":"{{token}}"}""");
        using var nul = await AccountApi.PostRaw(Client, TenancyApi.AcceptPath, $$"""{"token":"{{token}}","password":"Abcdefg1\u0000x"}""");

        await TenancyApi.AssertErrorAsync(missing, HttpStatusCode.BadRequest, "invalid_request");
        await TenancyApi.AssertErrorAsync(nul, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Null(await UserAsync(Address));
    }

    [Fact]
    public async Task A_token_that_is_a_non_blank_string_is_never_an_invalid_request_for_its_content()
    {
        foreach (var token in new[] { "x", "Zażółć gęślą jaźń", new string('t', 7000), "{\"a\":1}" })
        {
            using var preview = await TenancyApi.Preview(Client, token);
            await AssertInvalidTokenAsync(preview);
        }
    }

    // ---- accept

    [Fact]
    public async Task Accepting_for_an_address_without_an_account_creates_a_confirmed_member()   // criterion 4
    {
        var company = await CreateCompanyAsync("Acme");
        var (invite, token) = await MailedAsync(company);

        using var accepted = await TenancyApi.Accept(Client, token, Password);

        await TenancyApi.AssertEmptyAsync(accepted, HttpStatusCode.NoContent);
        var user = Assert.IsType<ApplicationUser>(await UserAsync(Address));
        Assert.True(user.EmailConfirmed);
        Assert.Equal(Address, user.Email);
        var membership = Assert.Single(await NonSeedMembershipsAsync());
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(company, membership.CompanyId);
        Assert.Equal(await RoleIdAsync(company, "user"), membership.RoleId);
        Assert.Null(await InviteAsync(invite));   // used up
    }

    [Fact]
    public async Task Accept_signs_nobody_in()   // spec 0005 → POST /auth/invites/accept
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        using var accepted = await TenancyApi.Accept(Client, token, Password);

        Assert.False(accepted.Headers.Contains("Set-Cookie"));
        Assert.Equal("", await accepted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_role_with_star_gives_every_permission_of_the_catalog_sorted_without_star()   // criterion 4
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company, role: "admin");
        using (var accepted = await TenancyApi.Accept(Client, token, Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        var session = await SessionApi.LoginAsync(Client, Address, Password);

        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(
            ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task An_address_with_letters_beyond_ascii_gets_an_account()
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company, "zażółć.gęślą@acme.test");

        using var accepted = await TenancyApi.Accept(Client, token, Password);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        _ = await SessionApi.LoginAsync(Client, "zażółć.gęślą@acme.test", Password);
    }

    [Fact]
    public async Task Accepting_for_an_account_without_a_company_replaces_its_password_and_ends_every_session()   // criteria 5, 13
    {
        var company = await CreateCompanyAsync("Acme");
        var leaver = await AddMemberAsync(company, Address, "user", confirmed: false);

        // Make the account a member with a session of its own, confirmed, then take the membership away.
        await InDbAsync(db => db.Users.Where(u => u.Id == leaver).ExecuteUpdateAsync(set => set.SetProperty(u => u.EmailConfirmed, true), TestContext.Current.CancellationToken));
        var session = await SessionApi.LoginAsync(Client, Address, UserPassword);
        await RemoveMembershipAsync(leaver);
        var (_, token) = await MailedAsync(company);

        using (var accepted = await TenancyApi.Accept(Client, token, Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        // The person is a member again, so only the end of the sessions can refuse this refresh.
        using var refreshed = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refreshed);
        using var oldPassword = await LoginApi.Login(Client, Address, UserPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        _ = await SessionApi.LoginAsync(Client, Address, Password);
        Assert.Single(await NonSeedMembershipsAsync());
    }

    [Fact]
    public async Task An_invitation_stored_for_a_look_alike_of_an_existing_address_is_an_invalid_token()   // defence in depth: sending refuses such an address
    {
        var company = await CreateCompanyAsync("Acme");
        await CreateUserAsync("steve@acme.test", confirmed: true, member: false);
        // A long s: a host with ICU normalises this address to the one of steve@acme.test. Made before the rule existed.
        var (invite, token) = await MailedAsync(company, "\u017Fteve@acme.test");

        using (var preview = await TenancyApi.Preview(Client, token))
        {
            await AssertInvalidTokenAsync(preview);
        }

        using (var accepted = await TenancyApi.Accept(Client, token, Password))
        {
            await AssertInvalidTokenAsync(accepted);
        }

        // Steve's password still holds (403: right password, no company) and nobody joined.
        using var login = await LoginApi.Login(Client, "steve@acme.test", UserPassword);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Empty(await NonSeedMembershipsAsync());
        Assert.NotNull(await InviteAsync(invite));
    }

    [Theory]
    [InlineData("\u03BCaria@acme.test", "\u00B5aria@acme.test")]   // Greek mu, and the micro sign: both upper-case to U+039C
    [InlineData("kosta\u03C2@acme.test", "kosta\u03C3@acme.test")]  // a final sigma, and a sigma: both upper-case to U+03A3
    [InlineData("\u03B8eo@acme.test", "\u03D1eo@acme.test")]       // theta, and the theta symbol
    public async Task An_invitation_for_another_spelling_of_an_existing_account_cannot_take_it_over(string account, string invited)   // the link proves the invited mailbox only
    {
        var company = await CreateCompanyAsync("Acme");
        var owner = await CreateUserAsync(account, confirmed: true, member: false);
        var (invite, token) = await MailedAsync(company, invited);

        using (var preview = await TenancyApi.Preview(Client, token))
        {
            await AssertInvalidTokenAsync(preview);
        }

        using (var accepted = await TenancyApi.Accept(Client, token, Password))
        {
            await AssertInvalidTokenAsync(accepted);
        }

        // The account keeps its password (403: right password, no company), joins nothing, and the invitation stays.
        using var login = await LoginApi.Login(Client, account, UserPassword);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Empty(await NonSeedMembershipsAsync());
        Assert.NotNull(await InviteAsync(invite));
        Assert.Equal(account, (await UserAsync(account))!.Email);
        Assert.Equal(owner.Id, (await UserAsync(account))!.Id);
    }

    [Fact]
    public async Task Another_spelling_of_a_member_of_another_company_is_not_told_already_member()
    {
        var company = await CreateCompanyAsync("Acme");
        var globex = await CreateCompanyAsync("Globex");
        await AddMemberAsync(globex, "\u03BCaria@globex.test", "user");
        var (_, token) = await MailedAsync(company, "\u00B5aria@globex.test");

        using var preview = await TenancyApi.Preview(Client, token);

        await AssertInvalidTokenAsync(preview);
    }

    [Fact]
    public async Task An_invitation_in_other_ascii_capitals_reaches_the_existing_account()
    {
        var company = await CreateCompanyAsync("Acme");
        var steve = await CreateUserAsync("Steve@acme.test", confirmed: true, member: false);
        var (_, token) = await MailedAsync(company, "steve@ACME.test");

        using (var accepted = await TenancyApi.Accept(Client, token, Password))
        {
            await TenancyApi.AssertEmptyAsync(accepted, HttpStatusCode.NoContent);
        }

        var membership = Assert.Single(await NonSeedMembershipsAsync());
        Assert.Equal(steve.Id, membership.UserId);
        Assert.Equal("Steve@acme.test", (await UserAsync("steve@acme.test"))!.Email);
        _ = await SessionApi.LoginAsync(Client, "Steve@acme.test", Password);
    }

    [Fact]
    public async Task Accepting_confirms_an_unconfirmed_account_lifts_a_lockout_and_removes_its_other_links()   // spec 0005 → Effects
    {
        await CreateUserAsync(Address, confirmed: false, member: false);
        await EnqueueAsync(MailKind.EmailVerification, Address);
        await DispatchAsync();
        Assert.Single(await InDbAsync(db => db.EmailTokens.ToListAsync(TestContext.Current.CancellationToken)));
        await LockoutApi.FailAsync(Client, Clock, Address, 10);   // locked out
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        using var accepted = await TenancyApi.Accept(Client, token, Password);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.True((await UserAsync(Address))!.EmailConfirmed);
        Assert.Empty(await InDbAsync(db => db.EmailTokens.ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        _ = await SessionApi.LoginAsync(Client, Address, Password);   // first try
    }

    [Fact]
    public async Task Accepting_for_an_address_without_an_account_ends_the_login_streak_of_the_address()   // spec 0005 → Effects: new accounts too
    {
        await LockoutApi.FailAsync(Client, Clock, Address, 10);   // no account: streaks exist for such addresses all the same
        Assert.Equal(1, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        using var accepted = await TenancyApi.Accept(Client, token, Password);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        _ = await SessionApi.LoginAsync(Client, Address, Password);   // first try
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Weak_password_names_the_broken_rules_changes_nothing_and_leaves_the_token_usable(bool accountExists)   // criterion 7
    {
        if (accountExists)
        {
            await CreateUserAsync(Address, confirmed: false, member: false);
        }

        var company = await CreateCompanyAsync("Acme");
        var (invite, token) = await MailedAsync(company);
        var hash = (await InviteAsync(invite))!.TokenHash;

        using (var weak = await TenancyApi.Accept(Client, token, "abc"))
        {
            var raw = await TenancyApi.ReadAsync(weak, HttpStatusCode.BadRequest);
            Assert.Equal("""{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}""", raw);
        }

        var kept = Assert.IsType<Invite>(await InviteAsync(invite));
        Assert.Equal(hash, kept.TokenHash);
        Assert.Empty(await NonSeedMembershipsAsync());
        var user = await UserAsync(Address);
        if (accountExists)
        {
            Assert.False(user!.EmailConfirmed);   // untouched
            using var unchanged = await LoginApi.Login(Client, Address, UserPassword);
            Assert.Equal(HttpStatusCode.Forbidden, unchanged.StatusCode);   // the old password still works, and still needs verifying
        }
        else
        {
            Assert.Null(user);
        }

        using var good = await TenancyApi.Accept(Client, token, Password);
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
    }

    [Fact]
    public async Task Of_parallel_accepts_with_one_token_exactly_one_succeeds()   // criterion 6
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        // Eight, not more: every waiting caller holds a server connection.
        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using var response = await TenancyApi.Accept(Client, token, Password);
            return response.StatusCode;
        })));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.NoContent));
        Assert.Equal(7, statuses.Count(s => s == HttpStatusCode.BadRequest));
        Assert.Single(await NonSeedMembershipsAsync());
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Address, TestContext.Current.CancellationToken)));
    }

    // ---- an address that belongs to a member of another company

    [Fact]
    public async Task For_a_member_of_another_company_preview_and_accept_say_already_member_and_the_invitation_stays()   // criterion 8
    {
        var acme = await CreateCompanyAsync("Acme");
        var globex = await CreateCompanyAsync("Globex");
        await AddMemberAsync(globex, Address, "user");
        var (invite, token) = await MailedAsync(acme);

        using (var preview = await TenancyApi.Preview(Client, token))
        {
            await TenancyApi.AssertErrorAsync(preview, HttpStatusCode.Conflict, "already_member");
        }

        using (var accept = await TenancyApi.Accept(Client, token, Password))
        {
            await TenancyApi.AssertErrorAsync(accept, HttpStatusCode.Conflict, "already_member");
        }

        Assert.NotNull(await InviteAsync(invite));                          // still usable until it expires
        using var login = await LoginApi.Login(Client, Address, UserPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);                  // the password was not replaced
        Assert.Equal(globex, (await NonSeedMembershipsAsync()).Single().CompanyId);
    }

    [Fact]
    public async Task A_weak_password_for_an_address_that_is_already_a_member_elsewhere_is_already_member()   // already_member comes before the password policy
    {
        var acme = await CreateCompanyAsync("Acme");
        var globex = await CreateCompanyAsync("Globex");
        await AddMemberAsync(globex, Address, "user");
        var (invite, token) = await MailedAsync(acme);

        using var response = await TenancyApi.Accept(Client, token, "abc");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "already_member");
        Assert.NotNull(await InviteAsync(invite));
    }

    [Fact]
    public async Task Accepting_one_invitation_leaves_the_others_of_the_address_as_they_are_and_they_then_say_already_member()   // spec 0005 → Effects
    {
        var acme = await CreateCompanyAsync("Acme");
        var globex = await CreateCompanyAsync("Globex");
        var (_, acmeToken) = await MailedAsync(acme);
        var (globexInvite, globexToken) = await MailedAsync(globex);

        using (var accepted = await TenancyApi.Accept(Client, acmeToken, Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        Assert.NotNull(await InviteAsync(globexInvite));
        using var second = await TenancyApi.Accept(Client, globexToken, "Another-Passw0rd");
        await TenancyApi.AssertErrorAsync(second, HttpStatusCode.Conflict, "already_member");
        _ = await SessionApi.LoginAsync(Client, Address, Password);   // the first password stands
        Assert.Equal(acme, Assert.Single(await NonSeedMembershipsAsync()).CompanyId);
    }

    [Fact]
    public async Task Two_invitations_of_two_companies_accepted_at_once_make_one_membership()
    {
        var acme = await CreateCompanyAsync("Acme");
        var globex = await CreateCompanyAsync("Globex");
        var (_, acmeToken) = await MailedAsync(acme);
        var (_, globexToken) = await MailedAsync(globex);

        var statuses = await Task.WhenAll(new[] { acmeToken, globexToken }.Select(token => Task.Run(async () =>
        {
            using var response = await TenancyApi.Accept(Client, token, Password);
            return response.StatusCode;
        })));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.NoContent));
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Single(await NonSeedMembershipsAsync());
        Assert.Equal(1, await InDbAsync(db => db.Invites.CountAsync(TestContext.Current.CancellationToken)));   // the refused one stays
    }

    // ---- what is never logged

    [Fact]
    public async Task No_token_password_or_address_reaches_the_log()   // criterion 23
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);
        using (await TenancyApi.Accept(Client, token, "abc"))
        {
        }

        using (await TenancyApi.Accept(Client, token, Password))
        {
        }

        using (await TenancyApi.Accept(Client, token, Password))
        {
        }

        Assert.DoesNotContain(token, Logs.Text);
        Assert.DoesNotContain(Password, Logs.Text);
        Assert.DoesNotContain(Address, Logs.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Answers_never_set_a_cookie_and_are_never_stored()
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        using var preview = await TenancyApi.Preview(Client, token);
        using var invalid = await TenancyApi.Preview(Client, "unknown");

        AccountApi.AssertNeverStoredAndNoCookie(preview);
        AccountApi.AssertNeverStoredAndNoCookie(invalid);
    }
}
