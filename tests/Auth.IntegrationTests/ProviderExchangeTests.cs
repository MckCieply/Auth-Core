using System.Net;
using System.Text.Json;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>POST /auth/providers/exchange</c> for a sign-in ticket (criteria 10 to 13).</summary>
public sealed class ProviderExchangeTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private const char Mu = (char)0x03BC;
    private const char Micro = (char)0x00B5;

    private static readonly string[] SessionClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"];
    private static readonly string[] SameClaims = ["aud", "iss", "org_id", "org_kind", "sub"];

    private async Task<Guid> LinkedAccountAsync(bool member = true)
    {
        var user = await CreateUserAsync(Ola, confirmed: true, member: member);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        return user.Id;
    }

    [Fact]
    public async Task A_linked_identity_signs_in_as_a_login_does()   // criterion 10
    {
        var userId = await LinkedAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Exchange(Client, ticket);

        var session = await SessionApi.ReadSessionAsync(response);
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(["access_token", "status"], body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal("authenticated", body.RootElement.GetProperty("status").GetString());
        }

        ProviderApi.AssertBindingCleared(response);
        var password = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        Assert.Equal(SessionClaims, AccessTokens.Payload(session.AccessToken).Select(p => p.Key).Order(StringComparer.Ordinal));
        foreach (var claim in SameClaims)
        {
            Assert.Equal(AccessTokens.Payload(password.AccessToken)[claim]!.ToJsonString(), AccessTokens.Payload(session.AccessToken)[claim]!.ToJsonString());
        }

        Assert.Equal(AccessTokens.Array(password.AccessToken, "roles"), AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(AccessTokens.Array(password.AccessToken, "permissions"), AccessTokens.Array(session.AccessToken, "permissions"));
        _ = await SessionApi.RefreshOk(Client, session.RefreshToken);   // the cookie refreshes like a login's

        var row = Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded), r => AuditApi.Text(r, "provider") is not null);
        Assert.Equal("google", AuditApi.Text(row, "provider"));
        Assert.Equal(userId, row.SubjectUserId);
        Assert.Equal(userId, row.ActorUserId);
        Assert.Equal(0, await TicketCountAsync());
    }

    [Fact]
    public async Task A_locked_login_streak_does_not_stop_a_linked_identity_and_is_not_cleared()   // criterion 10, Settled 12
    {
        await LinkedAccountAsync();
        await LockoutApi.FailAsync(Client, Clock, Ola, 10);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stillLocked = await LoginApi.Login(Client, Ola, UserPassword);
        await LockoutApi.AssertLockedAsync(stillLocked);
    }

    [Fact]
    public async Task A_linked_identity_whose_provider_address_changed_still_signs_in_to_its_account()   // criterion 11, Settled 10
    {
        var userId = await LinkedAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(email: "ola.new@gmail.test"));

        using var response = await ProviderApi.Exchange(Client, ticket);

        var session = await SessionApi.ReadSessionAsync(response);
        Assert.Equal(userId.ToString(), AccessTokens.Text(session.AccessToken, "sub"));
        var account = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken));
        Assert.Equal(Ola, account.Email);   // the account keeps the address it was made with
    }

    [Fact]
    public async Task An_unlinked_identity_with_the_address_of_a_linked_account_gets_link()   // criterion 11: the address never finds a linked account
    {
        await LinkedAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(subject: "200000000000000000002"));

        using var response = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal("""{"next":"link","email":"ola@gmail.test","email_verified":true}""", await TenancyApi.ReadAsync(response, HttpStatusCode.OK));
    }

    [Fact]
    public async Task A_verified_address_of_an_account_gives_link_and_the_ticket_stays_usable()   // criterion 12
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var first = await ProviderApi.Exchange(Client, ticket);
        using var again = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal("""{"next":"link","email":"ola@gmail.test","email_verified":true}""", await TenancyApi.ReadAsync(first, HttpStatusCode.OK));
        Assert.Equal("""{"next":"link","email":"ola@gmail.test","email_verified":true}""", await TenancyApi.ReadAsync(again, HttpStatusCode.OK));
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task An_account_under_a_look_alike_spelling_is_409_already_registered_and_the_ticket_stays()   // criterion 12, Settled 11
    {
        await CreateUserAsync(Mu + "aria@firma.test", confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson(email: Micro + "aria@firma.test", subject: "300000000000000000003"));

        using var response = await ProviderApi.Exchange(Client, ticket);

        await SignupApi.AssertAlreadyRegisteredAsync(response);
        Assert.Equal(1, await TicketCountAsync());
    }

    [Theory]
    [InlineData(true, true, "organization,private")]
    [InlineData(true, false, "organization")]
    [InlineData(false, true, "private")]
    public async Task No_account_with_sign_up_on_gives_finish_signup_with_the_kinds_whose_switch_is_on(bool organization, bool @private, string kinds)   // criterion 12
    {
        SetManifest((organization ? AuthAppFactory.SelfServiceManifest : AuthAppFactory.DefaultManifest) + (@private ? AuthAppFactory.PrivateBlock : ""));
        var ticket = await TicketAsync("google", GooglePerson(email: "new@gmail.test"));

        using var response = await ProviderApi.Exchange(Client, ticket);

        var body = await TenancyApi.ReadOkAsync(response);
        Assert.Equal(["email", "kinds", "next"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("finish_signup", body.GetProperty("next").GetString());
        Assert.Equal("new@gmail.test", body.GetProperty("email").GetString());
        Assert.Equal(kinds.Split(','), body.GetProperty("kinds").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task No_account_with_both_switches_off_is_403_no_account()   // criterion 12
    {
        SetManifest(AuthAppFactory.DefaultManifest);
        var ticket = await TicketAsync("google", GooglePerson(email: "new@gmail.test"));

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_account");
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task An_unverified_work_address_of_an_account_gets_link_without_a_word_about_the_account()   // criterion 13, Review Focus 5
    {
        await CreateUserAsync(FakePeople.WorkEmail, confirmed: true);
        var withAccount = await TicketAsync("microsoft", WorkMicrosoft());
        var withoutAccount = await TicketAsync("microsoft", WorkMicrosoft(email: "nobody@firma.test", subject: "00000000-0000-0000-0000-00000000b002"));

        using var one = await ProviderApi.Exchange(Client, withAccount);
        using var two = await ProviderApi.Exchange(Client, withoutAccount);

        Assert.Equal("""{"next":"link","email":"bo@firma.test","email_verified":false}""", await TenancyApi.ReadAsync(one, HttpStatusCode.OK));
        Assert.Equal("""{"next":"link","email":"nobody@firma.test","email_verified":false}""", await TenancyApi.ReadAsync(two, HttpStatusCode.OK));
        Assert.Equal(LoginApi.HeaderNames(one), LoginApi.HeaderNames(two));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task No_address_is_403_no_email()   // criterion 13
    {
        var ticket = await TicketAsync("google", GooglePerson(defect: FakeDefects.NoEmail));

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_email");
    }

    [Fact]
    public async Task A_linked_account_without_a_membership_is_403_no_membership_and_the_ticket_stays()   // spec 0011 → exchange, step 3.1
    {
        await LinkedAccountAsync(member: false);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(1, await TicketCountAsync());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"ticket":5}""")]
    [InlineData("""{"ticket":null}""")]
    [InlineData("""["ticket"]""")]
    public async Task A_body_that_is_not_an_object_with_a_ticket_string_is_400_invalid_request(string body)   // spec 0011 → exchange, step 1
    {
        using var response = await AccountApi.PostRaw(Client, ProviderApi.ExchangePath, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_ticket_holding_an_unpaired_surrogate_escape_is_400_invalid_request()   // spec 0011 → exchange, step 1
    {
        var escape = (char)92 + "uD800";   // built in code: the JSON text of a lone high surrogate
        using var response = await AccountApi.PostRaw(Client, ProviderApi.ExchangePath, "{\"ticket\":\"a" + escape + "b\"}");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_ticket_is_a_string_and_so_an_unknown_ticket(string ticket)   // spec 0011 → exchange: only "missing or not a string" is step 1
    {
        var live = await TicketAsync("google", GooglePerson());   // a live ticket and its binding: the blank one is still unknown

        using var response = await ProviderApi.Exchange(Client, ticket, live.Binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task An_invitation_ticket_is_invalid_ticket_until_invitations_are_built()   // replaced when the invitation branch is built
    {
        var ticket = await TicketAsync("google", GooglePerson(), "?invite=" + "CCCCCCCCCC" + "CCCCCCCCCC" + "CCCCCCCCCC" + "CCCCCCCCCC" + "CCC");

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
    }
}
