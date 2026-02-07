using System.Net;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class OrgInviteTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string InvitesPath = "/auth/org/invites";
    private const string Boss = "boss@acme.test";
    private const string Worker = "worker@acme.test";

    /// <summary>A company "Acme" with an admin, who is logged in.</summary>
    private async Task<(Guid Company, Guid AdminId, string Token)> AcmeAsync()
    {
        var company = await CreateCompanyAsync("Acme");
        var admin = await AddMemberAsync(company, Boss, "admin");
        return (company, admin, (await SessionApi.LoginAsync(Client, Boss, UserPassword)).AccessToken);
    }

    private async Task<HttpResponseMessage> InviteAsync(string token, string email, Guid roleId) =>
        await TenancyApi.Send(Client, HttpMethod.Post, InvitesPath, token, new { email, role_id = roleId });

    private async Task<string> InviteOkAsync(string token, string email, Guid roleId)
    {
        using var response = await InviteAsync(token, email, roleId);
        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        return email;
    }

    private async Task<System.Text.Json.JsonElement> ListAsync(string token)
    {
        using var response = await TenancyApi.Get(Client, InvitesPath, token);
        return await TenancyApi.ReadOkAsync(response);
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    private Task<List<Invite>> InvitesAsync() =>
        InDbAsync(db => db.Invites.AsNoTracking().OrderBy(i => i.Email).ToListAsync(TestContext.Current.CancellationToken));

    private Task<List<MailRequest>> QueueAsync() =>
        InDbAsync(db => db.MailRequests.AsNoTracking().OrderBy(r => r.Id).ToListAsync(TestContext.Current.CancellationToken));

    private static async Task<string> FingerprintAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => !string.Equals(h.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}: {string.Join(",", h.Value)}");
        return $"{(int)response.StatusCode}\n{string.Join("\n", headers)}\n{await response.Content.ReadAsStringAsync()}";
    }

    // ---- sending

    [Fact]
    public async Task Manager_invites_an_address_and_a_mail_follows()   // criterion 2
    {
        var (company, _, token) = await AcmeAsync();

        await InviteOkAsync(token, Worker, await RoleIdAsync(company, "user"));
        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Worker, mail.To);
        Assert.Contains(AuthAppFactory.DefaultInviteUrl + "?token=", mail.TextBody);
        Assert.Contains("Acme", mail.TextBody);
        Assert.Contains("as user", mail.TextBody);
        Assert.Contains("valid for 7 days", mail.TextBody);
    }

    [Fact]
    public async Task Invitation_records_the_inviter_the_address_as_typed_and_the_role()
    {
        var (company, admin, token) = await AcmeAsync();

        await InviteOkAsync(token, "Anna.Worker@Acme.Test", await RoleIdAsync(company, "user"));

        var invite = Assert.Single(await InvitesAsync());
        Assert.Equal("Anna.Worker@Acme.Test", invite.Email);
        Assert.Equal("ANNA.WORKER@ACME.TEST", invite.NormalizedEmail);
        Assert.Equal(admin, invite.InvitedBy);
        Assert.Equal(company, invite.CompanyId);
        Assert.Equal(await RoleIdAsync(company, "user"), invite.RoleId);
        Assert.Null(invite.TokenHash);   // no token until the mail is composed
        Assert.Equal(StorableTime.Now(Clock) + InviteTokens.Lifetime, invite.ExpiresAt);
        var queued = Assert.Single(await QueueAsync());
        Assert.Equal(MailKind.Invitation, queued.Kind);
        Assert.Equal(invite.Id, queued.InviteId);
    }

    [Fact]
    public async Task Answer_is_the_same_for_every_address_outside_the_company()   // criterion 8
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");
        var elsewhere = await CreateCompanyAsync("Globex");
        await AddMemberAsync(elsewhere, "carol@globex.test", "user");
        await AddMemberAsync(elsewhere, "dave@globex.test", "user", confirmed: false);
        await CreateUserAsync("erin@nowhere.test", confirmed: true, member: false);

        var answers = new List<string>();
        foreach (var address in new[] { "nobody@nowhere.test", "carol@globex.test", "dave@globex.test", "erin@nowhere.test" })
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            using var response = await InviteAsync(token, address, user);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            answers.Add(await FingerprintAsync(response));
        }

        // The same status, headers and body: the inviter learns nothing about the addresses.
        Assert.Single(answers.Distinct());
        // The same effects, too: an invitation and a queued mail each.
        Assert.Equal(4, (await InvitesAsync()).Count);
        Assert.Equal(4, (await QueueAsync()).Count);
    }

    [Fact]
    public async Task Inviting_a_member_of_the_own_company_is_already_in_org_whatever_the_spelling()   // criterion 9
    {
        var (company, _, token) = await AcmeAsync();
        await AddMemberAsync(company, Worker, "user");
        var user = await RoleIdAsync(company, "user");

        foreach (var spelling in new[] { Worker, "WORKER@ACME.TEST", "Worker@Acme.Test" })
        {
            using var response = await InviteAsync(token, spelling, user);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "already_in_org");
        }

        Assert.Empty(await InvitesAsync());
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Already_in_org_is_answered_before_the_pending_check_and_the_mail_limit()   // spec 0005 → the order of checks
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");
        await InviteOkAsync(token, Worker, user);   // a pending invitation, and a mail limit row that is still hot
        await AddMemberAsync(company, Worker, "user");

        using var response = await InviteAsync(token, Worker, user);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "already_in_org");   // not invite_pending, not 429
    }

    [Fact]
    public async Task Inviting_an_address_with_a_pending_invitation_is_invite_pending()   // criterion 9
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");
        await InviteOkAsync(token, Worker, user);

        using var again = await InviteAsync(token, Worker, user);
        using var respelled = await InviteAsync(token, "WORKER@acme.test", await RoleIdAsync(company, "admin"));

        await TenancyApi.AssertErrorAsync(again, HttpStatusCode.Conflict, "invite_pending");
        await TenancyApi.AssertErrorAsync(respelled, HttpStatusCode.Conflict, "invite_pending");
        Assert.Single(await InvitesAsync());
        Assert.Single(await QueueAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_invitation_is_not_pending_and_is_replaced(bool mailed)   // criterion 9
    {
        var (company, _, _) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");
        var token = (await SessionApi.LoginAsync(Client, Boss, UserPassword)).AccessToken;
        await InviteOkAsync(token, Worker, user);
        if (mailed)
        {
            await DispatchAsync();
        }

        Clock.Advance(InviteTokens.Lifetime);   // exactly seven days from the mail (or from the invitation, if it was never mailed)
        var fresh = (await SessionApi.LoginAsync(Client, Boss, UserPassword)).AccessToken;
        Assert.Empty(ListItems(await ListAsync(fresh)));   // it has disappeared from the list

        await InviteOkAsync(fresh, Worker, user);

        var invite = Assert.Single(await InvitesAsync());
        Assert.Equal(StorableTime.Now(Clock) + InviteTokens.Lifetime, invite.ExpiresAt);
        Assert.Single(ListItems(await ListAsync(fresh)));
    }

    private static List<System.Text.Json.JsonElement> ListItems(System.Text.Json.JsonElement body) =>
        [.. body.GetProperty("invites").EnumerateArray()];

    [Fact]
    public async Task Role_must_belong_to_the_callers_company()   // criterion 14
    {
        var (_, _, token) = await AcmeAsync();
        var elsewhere = await CreateCompanyAsync("Globex");

        using var foreign = await InviteAsync(token, Worker, await RoleIdAsync(elsewhere, "user"));
        using var unknown = await InviteAsync(token, Worker, Guid.NewGuid());

        await TenancyApi.AssertErrorAsync(foreign, HttpStatusCode.NotFound, "not_found");
        await TenancyApi.AssertErrorAsync(unknown, HttpStatusCode.NotFound, "not_found");
        Assert.Empty(await InvitesAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"email":"a@acme.test"}""")]
    [InlineData("""{"role_id":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"email":"a@acme.test","role_id":"not-a-uuid"}""")]
    [InlineData("""{"email":"a@acme.test","role_id":"11111111111111111111111111111111"}""")]       // a UUID without its hyphens is not the usual form
    [InlineData("""{"email":"a@acme.test","role_id":"{11111111-1111-1111-1111-111111111111}"}""")]
    [InlineData("""{"email":"a@acme.test","role_id":42}""")]
    [InlineData("""{"email":42,"role_id":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"email":"   ","role_id":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"email":"bob.acme.test","role_id":"11111111-1111-1111-1111-111111111111"}""")]            // not an address
    [InlineData("""{"email":"Bob <bob@acme.test>","role_id":"11111111-1111-1111-1111-111111111111"}""")]     // a name and an address
    [InlineData("""{"email":"a@acme.test, b@acme.test","role_id":"11111111-1111-1111-1111-111111111111"}""")] // two addresses
    [InlineData("""{"email":"a\u0007b@acme.test","role_id":"11111111-1111-1111-1111-111111111111"}""")]        // a control character
    [InlineData("""{"email":"a\uFFFEb@acme.test","role_id":"11111111-1111-1111-1111-111111111111"}""")]        // a noncharacter
    [InlineData("""["a@acme.test"]""")]
    [InlineData("not json")]
    public async Task Malformed_request_is_a_400_and_changes_nothing(string body)
    {
        var (_, _, token) = await AcmeAsync();
        using var request = TenancyApi.Request(HttpMethod.Post, InvitesPath, token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Empty(await InvitesAsync());
        Assert.Empty(await QueueAsync());
    }

    [Theory]
    [InlineData("\u017Fteve@acme.test")]   // a long s: a host with ICU upper-cases it to the S of steve@acme.test
    [InlineData("\u212Aate@acme.test")]    // the Kelvin sign: its lower case is k
    [InlineData("joe@[10.0.0.5]")]          // a domain literal
    [InlineData("joe@[IPv6:::1]")]
    [InlineData("joe@localhost")]           // a domain without a dot
    [InlineData("joe@intranet")]
    [InlineData("joe@10.0.0.5")]            // an IP address without its brackets
    [InlineData("joe@intranet.\u00AD")]     // a soft hyphen, which IDNA drops: intranet. would be sent
    [InlineData("joe@10.0.0.\uFF15")]       // a fullwidth 5, which IDNA maps: 10.0.0.5 would be sent
    public async Task An_address_that_could_stand_for_another_account_or_reach_an_internal_host_is_a_400(string email)
    {
        var (company, _, token) = await AcmeAsync();

        using var response = await InviteAsync(token, email, await RoleIdAsync(company, "user"));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Empty(await InvitesAsync());
        Assert.Empty(await QueueAsync());
    }

    [Theory]
    [InlineData("worker@acme.test")]
    [InlineData("zażółć.gęślą@acme.test")]   // letters beyond ASCII that fold to none
    [InlineData("Łukasz@poczta.example.pl")]
    public async Task An_ordinary_address_is_invited(string email)
    {
        var (company, _, token) = await AcmeAsync();

        await InviteOkAsync(token, email, await RoleIdAsync(company, "user"));

        Assert.Equal(email, Assert.Single(await InvitesAsync()).Email);
    }

    [Fact]
    public async Task Address_longer_than_254_characters_is_a_400()
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");

        using (var ok = await InviteAsync(token, new string('a', 244) + "@acme.test", user))   // exactly 254
        {
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        Clock.Advance(TimeSpan.FromSeconds(61));
        using var over = await InviteAsync(token, new string('b', 245) + "@acme.test", user);   // 255
        await TenancyApi.AssertErrorAsync(over, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Request_with_the_wrong_content_type_is_a_400()
    {
        var (company, _, token) = await AcmeAsync();
        using var request = TenancyApi.Request(HttpMethod.Post, InvitesPath, token);
        request.Content = new StringContent($$"""{"email":"{{Worker}}","role_id":"{{await RoleIdAsync(company, "user")}}"}""", Encoding.UTF8, "text/plain");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    // ---- the mail limit

    [Fact]
    public async Task Inviting_again_within_a_minute_of_cancelling_is_refused_with_the_time_left()   // criterion 10
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");
        await InviteOkAsync(token, Worker, user);
        var id = Assert.Single(await InvitesAsync()).Id;
        using (var cancelled = await TenancyApi.Send(Client, HttpMethod.Delete, $"{InvitesPath}/{id}", token))
        {
            await TenancyApi.AssertEmptyAsync(cancelled, HttpStatusCode.NoContent);
        }

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var refused = await InviteAsync(token, Worker, user);

        var seconds = await LockoutApi.AssertLockedAsync(refused);
        Assert.Equal(40, seconds);
        Assert.Empty(await InvitesAsync());   // a refused invitation is not left behind
        Assert.Single(await QueueAsync());
    }

    [Fact]
    public async Task One_company_cannot_block_or_observe_the_invitations_of_another_to_the_same_person()   // criterion 10
    {
        var (acme, _, acmeToken) = await AcmeAsync();
        var globex = await CreateCompanyAsync("Globex");
        await AddMemberAsync(globex, "boss@globex.test", "admin");
        var globexToken = (await SessionApi.LoginAsync(Client, "boss@globex.test", UserPassword)).AccessToken;

        await InviteOkAsync(acmeToken, Worker, await RoleIdAsync(acme, "user"));
        await InviteOkAsync(globexToken, Worker, await RoleIdAsync(globex, "user"));   // not refused: its own limit

        var invites = await InvitesAsync();
        Assert.Equal(new[] { acme, globex }.Order(), invites.Select(i => i.CompanyId).Order());
        Assert.Equal(2, (await InDbAsync(db => db.MailRequestLimits.Where(l => l.Kind == MailKind.Invitation).CountAsync(TestContext.Current.CancellationToken))));
    }

    [Fact]
    public async Task Sixth_mail_within_the_hour_is_refused_until_the_hour_is_over()   // criterion 10
    {
        var (company, _, token) = await AcmeAsync();
        await InviteOkAsync(token, Worker, await RoleIdAsync(company, "user"));
        var id = Assert.Single(await InvitesAsync()).Id;

        for (var i = 0; i < 4; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(61));
            using var resent = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{id}/resend", token);
            await TenancyApi.AssertEmptyAsync(resent, HttpStatusCode.Accepted);
        }

        Clock.Advance(TimeSpan.FromSeconds(61));
        using var sixth = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{id}/resend", token);
        var seconds = await LockoutApi.AssertLockedAsync(sixth);
        Assert.Equal(3600 - (5 * 61), seconds);   // the window opened at the first mail, five minutes ago
        Assert.Equal(5, (await QueueAsync()).Count);
    }

    // ---- safety rule 1

    [Fact]
    public async Task Nobody_invites_with_a_role_that_holds_a_permission_they_lack()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        var manager = await AddRoleAsync(company, "manager", "members:manage", "reports:read");
        await AddMemberAsync(company, "mgr@acme.test", "manager");
        var plain = await AddRoleAsync(company, "plain");
        var narrower = await AddRoleAsync(company, "narrower", "reports:read");
        var wider = await AddRoleAsync(company, "wider", "reports:read", "reports:approve");
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;

        using var tooMuch = await InviteAsync(token, "a@acme.test", wider);                      // reports:approve is not theirs
        using var star = await InviteAsync(token, "b@acme.test", await RoleIdAsync(company, "admin"));
        await TenancyApi.AssertErrorAsync(tooMuch, HttpStatusCode.Forbidden, "permission_not_held");
        await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        Assert.Empty(await InvitesAsync());

        await InviteOkAsync(token, "c@acme.test", plain);        // nothing at all
        await InviteOkAsync(token, "d@acme.test", narrower);     // a subset
        await InviteOkAsync(token, "e@acme.test", manager);      // their own role
    }

    [Fact]
    public async Task A_role_with_star_needs_a_caller_whose_role_holds_star_even_if_it_lists_everything()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        var everything = await AddRoleAsync(company, "everything", [.. Holder.Current.Catalog.Permissions]);
        await AddMemberAsync(company, "all@acme.test", "everything");
        var token = (await SessionApi.LoginAsync(Client, "all@acme.test", UserPassword)).AccessToken;

        using var star = await InviteAsync(token, "a@acme.test", await RoleIdAsync(company, "admin"));
        await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        await InviteOkAsync(token, "b@acme.test", everything);   // the same permissions, listed one by one
    }

    [Fact]
    public async Task An_admin_with_star_may_invite_with_star()   // criterion 15
    {
        var (company, _, token) = await AcmeAsync();

        await InviteOkAsync(token, "a@acme.test", await RoleIdAsync(company, "admin"));
    }

    [Fact]
    public async Task The_operator_is_exempt_from_rule_1_and_is_recorded_as_no_member()   // criterion 2
    {
        var company = await CreateCompanyAsync("Acme");
        var admin = await RoleIdAsync(company, "admin");
        using var scope = Factory.Services.CreateScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<InvitationService>()
            .SendAsync(Actor.Operator, company, "first@acme.test", "FIRST@ACME.TEST", admin, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Error);
        var invite = Assert.Single(await InvitesAsync());
        Assert.Null(invite.InvitedBy);
        Assert.Equal(1, await DispatchAsync());
        Assert.Equal("first@acme.test", Assert.Single(Mail.Sent).To);
    }

    [Fact]
    public async Task Nobody_resends_or_cancels_an_invitation_whose_role_holds_more_than_they_do()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage", "reports:read");
        await AddMemberAsync(company, "mgr@acme.test", "manager");
        await AddRoleAsync(company, "wider", "reports:read", "reports:approve");
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;
        var wider = await AddInviteAsync(company, "a@acme.test", "wider");
        var star = await AddInviteAsync(company, "b@acme.test", "admin");

        foreach (var id in new[] { wider, star })
        {
            using var resend = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{id}/resend", token);
            await TenancyApi.AssertErrorAsync(resend, HttpStatusCode.Forbidden, "permission_not_held");
            using var cancel = await TenancyApi.Send(Client, HttpMethod.Delete, $"{InvitesPath}/{id}", token);
            await TenancyApi.AssertErrorAsync(cancel, HttpStatusCode.Forbidden, "permission_not_held");
        }

        Assert.Equal(2, (await InvitesAsync()).Count);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task A_caller_resends_and_cancels_invitations_whose_role_holds_what_they_hold_or_less()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage", "reports:read");
        await AddMemberAsync(company, "mgr@acme.test", "manager");
        await AddRoleAsync(company, "narrower", "reports:read");
        await AddRoleAsync(company, "plain");
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;
        var own = await AddInviteAsync(company, "a@acme.test", "manager");
        var subset = await AddInviteAsync(company, "b@acme.test", "narrower");
        var nothing = await AddInviteAsync(company, "c@acme.test", "plain");

        foreach (var id in new[] { own, subset })
        {
            using var resend = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{id}/resend", token);
            await TenancyApi.AssertEmptyAsync(resend, HttpStatusCode.Accepted);
        }

        foreach (var id in new[] { own, subset, nothing })
        {
            using var cancel = await TenancyApi.Send(Client, HttpMethod.Delete, $"{InvitesPath}/{id}", token);
            await TenancyApi.AssertEmptyAsync(cancel, HttpStatusCode.NoContent);
        }

        Assert.Empty(await InvitesAsync());
    }

    [Fact]
    public async Task The_operator_resends_and_cancels_any_invitation()   // the operator is exempt from rule 1
    {
        var company = await CreateCompanyAsync("Acme");
        var star = await AddInviteAsync(company, "a@acme.test", "admin");
        var other = await AddInviteAsync(company, "b@acme.test", "admin");

        var resend = await InScopeAsync(sp => sp.GetRequiredService<InvitationService>()
            .ResendAsync(Actor.Operator, company, star, TestContext.Current.CancellationToken));
        var cancel = await InScopeAsync(sp => sp.GetRequiredService<InvitationService>()
            .CancelAsync(Actor.Operator, company, other, TestContext.Current.CancellationToken));

        Assert.True(resend.Succeeded, resend.Error);
        Assert.True(cancel.Succeeded, cancel.Error);
        Assert.Equal(star, Assert.Single(await InvitesAsync()).Id);
    }

    // ---- listing

    [Fact]
    public async Task List_shows_the_pending_invitations_sorted_by_address_with_their_roles_and_times()
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");
        await InviteOkAsync(token, "zed@acme.test", user);
        Clock.Advance(TimeSpan.FromSeconds(1));
        await InviteOkAsync(token, "Bob@acme.test", await RoleIdAsync(company, "admin"));
        Clock.Advance(TimeSpan.FromSeconds(1));
        await InviteOkAsync(token, "amy@acme.test", user);
        await DispatchAsync();   // some mailed, some not: all are listed

        var list = await ListAsync(token);

        Assert.Equal(["invites"], list.EnumerateObject().Select(p => p.Name));
        var items = ListItems(list);
        Assert.Equal(["Bob@acme.test", "amy@acme.test", "zed@acme.test"], items.Select(i => i.GetProperty("email").GetString()));   // ordinal: upper case first
        Assert.Equal(["email", "expires_at", "id", "invited_at", "role"], items[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["id", "name"], items[0].GetProperty("role").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("admin", items[0].GetProperty("role").GetProperty("name").GetString());
        Assert.Equal((await RoleIdAsync(company, "admin")).ToString(), items[0].GetProperty("role").GetProperty("id").GetString());
        Assert.EndsWith("Z", items[0].GetProperty("invited_at").GetString());     // ISO 8601, UTC
        Assert.EndsWith("Z", items[0].GetProperty("expires_at").GetString());
        Assert.True(items[0].GetProperty("expires_at").GetDateTimeOffset() > items[0].GetProperty("invited_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task List_holds_nothing_of_another_company()   // criterion 14
    {
        var (_, _, token) = await AcmeAsync();
        var globex = await CreateCompanyAsync("Globex");
        await AddInviteAsync(globex, "secret@globex.test", "user");

        Assert.Empty(ListItems(await ListAsync(token)));
    }

    // ---- resending and cancelling

    [Fact]
    public async Task Resend_queues_a_new_mail_and_the_new_link_replaces_the_old()   // criterion 10
    {
        var (company, _, token) = await AcmeAsync();
        await InviteOkAsync(token, Worker, await RoleIdAsync(company, "user"));
        await DispatchAsync();
        var first = TokenIn(Assert.Single(Mail.Sent));
        var id = Assert.Single(await InvitesAsync()).Id;

        Clock.Advance(TimeSpan.FromSeconds(61));
        using (var resent = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{id}/resend", token))
        {
            await TenancyApi.AssertEmptyAsync(resent, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var second = TokenIn(Mail.Sent[1]);
        Assert.NotEqual(first, second);
        Assert.Equal(EmailTokens.HashOf(second), Assert.Single(await InvitesAsync()).TokenHash);
    }

    [Fact]
    public async Task Resend_of_an_invitation_that_is_unknown_expired_or_of_another_company_is_404()   // criterion 14
    {
        var (_, _, token) = await AcmeAsync();
        var globex = await CreateCompanyAsync("Globex");
        var foreign = await AddInviteAsync(globex, "x@globex.test", "user");

        foreach (var id in new[] { Guid.NewGuid(), foreign })
        {
            using var response = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{id}/resend", token);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Resend_of_an_expired_invitation_is_404()
    {
        var (company, _, _) = await AcmeAsync();
        var invite = await AddInviteAsync(company, Worker, "user");
        Clock.Advance(InviteTokens.Lifetime);
        var token = (await SessionApi.LoginAsync(Client, Boss, UserPassword)).AccessToken;

        using var response = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{invite}/resend", token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Cancel_removes_the_invitation_and_drops_its_queued_mail()   // criterion 10
    {
        var (company, _, token) = await AcmeAsync();
        await InviteOkAsync(token, Worker, await RoleIdAsync(company, "user"));
        var id = Assert.Single(await InvitesAsync()).Id;

        using (var cancelled = await TenancyApi.Send(Client, HttpMethod.Delete, $"{InvitesPath}/{id}", token))   // at once: cancelling is not limited
        {
            await TenancyApi.AssertEmptyAsync(cancelled, HttpStatusCode.NoContent);
        }

        Assert.Empty(ListItems(await ListAsync(token)));
        Assert.Empty(await InvitesAsync());
        await DispatchAsync();
        Assert.Empty(Mail.Sent);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Cancel_of_an_invitation_that_is_unknown_gone_or_of_another_company_is_404()   // criterion 14
    {
        var (company, _, token) = await AcmeAsync();
        var globex = await CreateCompanyAsync("Globex");
        var foreign = await AddInviteAsync(globex, "x@globex.test", "user");
        var own = await AddInviteAsync(company, Worker, "user");

        using (var first = await TenancyApi.Send(Client, HttpMethod.Delete, $"{InvitesPath}/{own}", token))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        foreach (var id in new[] { own, Guid.NewGuid(), foreign })
        {
            using var response = await TenancyApi.Send(Client, HttpMethod.Delete, $"{InvitesPath}/{id}", token);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        Assert.NotNull(await InviteAsync(foreign));   // the other company's invitation is untouched
    }

    [Theory]
    [InlineData("POST", "not-a-uuid", "/resend")]
    [InlineData("DELETE", "not-a-uuid", "")]
    [InlineData("POST", "11111111-1111-1111-1111-111111111111%20", "/resend")]   // white space is not part of a UUID
    [InlineData("DELETE", "%2011111111-1111-1111-1111-111111111111", "")]
    [InlineData("DELETE", "11111111-1111-1111-1111-111111111111%09", "")]
    public async Task An_id_that_is_not_a_uuid_is_a_400(string method, string id, string suffix)
    {
        var (_, _, token) = await AcmeAsync();

        using var response = await TenancyApi.Send(Client, new HttpMethod(method), $"{InvitesPath}/{id}{suffix}", token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    // ---- who may

    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "")]
    [InlineData("POST", "/11111111-1111-1111-1111-111111111111/resend")]
    [InlineData("DELETE", "/11111111-1111-1111-1111-111111111111")]
    public async Task Every_endpoint_needs_a_token_and_members_manage_checked_against_the_database(string method, string suffix)   // criterion 14
    {
        var company = await CreateCompanyAsync("Acme");
        var worker = await AddMemberAsync(company, Worker, "admin");
        var session = await SessionApi.LoginAsync(Client, Worker, UserPassword);
        var path = InvitesPath + suffix;
        object? body = method == "POST" && suffix == "" ? new { email = "a@acme.test", role_id = Guid.NewGuid() } : null;

        using (var anonymous = await TenancyApi.Send(Client, new HttpMethod(method), path, null, body))
        {
            await TenancyApi.AssertUnauthorizedAsync(anonymous);
        }

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "user"));   // demoted after the token was issued
        using (var stale = await TenancyApi.Send(Client, new HttpMethod(method), path, session.AccessToken, body))
        {
            await TenancyApi.AssertErrorAsync(stale, HttpStatusCode.Forbidden, "permissions_changed");
        }

        var fresh = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using var plain = await TenancyApi.Send(Client, new HttpMethod(method), path, fresh.AccessToken, body);
        await TenancyApi.AssertErrorAsync(plain, HttpStatusCode.Forbidden, "forbidden");
    }

    // ---- races

    [Fact]
    public async Task Parallel_invitations_of_one_address_leave_one_invitation_and_one_mail()
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");

        // Six, not more: every waiting caller holds a server connection.
        var statuses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var response = await InviteAsync(token, Worker, user);
            return response.StatusCode;
        })));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Accepted));
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Single(await InvitesAsync());
        Assert.Single(await QueueAsync());
    }

    [Fact]
    public async Task A_caller_demoted_before_the_lock_cannot_resend_or_cancel()   // safety rule 2: the caller is read again under the lock
    {
        var (company, boss, _) = await AcmeAsync();
        var invite = await AddInviteAsync(company, Worker, "user", boss);
        var stale = Actor.Of(await TenantOfAsync(boss));
        await SetMemberRoleAsync(boss, await RoleIdAsync(company, "user"));   // demoted after the endpoint read them

        var resend = await InScopeAsync(sp => sp.GetRequiredService<InvitationService>()
            .ResendAsync(stale, company, invite, TestContext.Current.CancellationToken));
        var cancel = await InScopeAsync(sp => sp.GetRequiredService<InvitationService>()
            .CancelAsync(stale, company, invite, TestContext.Current.CancellationToken));

        Assert.Equal("permissions_changed", resend.Error);
        Assert.Equal("permissions_changed", cancel.Error);
        Assert.NotNull(await InviteAsync(invite));
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Responses_are_never_stored_and_set_no_cookie()
    {
        var (company, _, token) = await AcmeAsync();

        using var accepted = await InviteAsync(token, Worker, await RoleIdAsync(company, "user"));
        using var listed = await TenancyApi.Get(Client, InvitesPath, token);

        AccountApi.AssertNeverStoredAndNoCookie(accepted);
        AccountApi.AssertNeverStoredAndNoCookie(listed);
    }
}
