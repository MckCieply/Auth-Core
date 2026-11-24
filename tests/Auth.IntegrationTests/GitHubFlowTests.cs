using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012, criterion 6: a GitHub ticket through the exchange, the link, the finish and the invitation, as spec 0011 says for Google.</summary>
public sealed class GitHubFlowTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys, github: true)
{
    private const string Ola = FakePeople.GitHubEmail;

    private Task<List<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>> GitHubLoginsAsync() =>
        InDbAsync(db => db.UserLogins.AsNoTracking().Where(l => l.LoginProvider == "github").ToListAsync(TestContext.Current.CancellationToken));

    private static async Task<string?> NextOfAsync(HttpResponseMessage exchanged) =>
        (await TenancyApi.ReadOkAsync(exchanged)).GetProperty("next").GetString();

    [Fact]
    public async Task A_linked_github_identity_signs_in_through_the_exchange_with_provider_github()   // spec 0011, criterion 10
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "github", FakePeople.GitHubId);

        using var exchanged = await ProviderApi.Exchange(Client, await TicketAsync("github", GitHubPerson()));

        var session = await SessionApi.ReadSessionAsync(exchanged);
        Assert.Equal(user.Id.ToString(), AccessTokens.Text(session.AccessToken, "sub"));
        Assert.Equal("github", AuditApi.Text(await SingleAsync(AuditKinds.LoginSucceeded), "provider"));
    }

    [Fact]
    public async Task An_unlinked_identity_with_the_address_of_an_account_is_next_link_and_the_link_adds_the_decimal_id()   // spec 0011, criteria 11 and 15
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("github", GitHubPerson());

        using (var exchanged = await ProviderApi.Exchange(Client, ticket))
        {
            Assert.Equal("link", await NextOfAsync(exchanged));
        }

        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var login = Assert.Single(await GitHubLoginsAsync());
        Assert.Equal((user.Id, FakePeople.GitHubId, "GitHub"), (login.UserId, login.ProviderKey, login.ProviderDisplayName));
        var row = await SingleAsync(AuditKinds.ProviderLinked);
        Assert.Equal(("github", "sign_in"), (AuditApi.Text(row, "provider"), AuditApi.Text(row, "via")));
    }

    [Theory]
    [InlineData("private")]
    [InlineData("organization")]
    public async Task A_github_identity_with_no_account_finishes_the_sign_up(string kind)   // spec 0011, criteria 12, 13 and 19
    {
        var ticket = await TicketAsync("github", GitHubPerson());

        using (var exchanged = await ProviderApi.Exchange(Client, ticket))
        {
            Assert.Equal("finish_signup", await NextOfAsync(exchanged));
        }

        using var finished = await ProviderApi.Finish(Client, ticket, kind, kind == "organization" ? "Firma" : null);

        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal(FakePeople.GitHubId, Assert.Single(await GitHubLoginsAsync()).ProviderKey);
        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal(("github", kind), (AuditApi.Text(completed, "provider"), AuditApi.Text(completed, "kind")));
        Assert.Equal("github", AuditApi.Text(await SingleAsync(AuditKinds.LoginSucceeded), "provider"));
    }

    [Fact]
    public async Task A_github_person_without_a_primary_address_is_403_no_email()   // spec 0011, criterion 11; spec 0012, criterion 4
    {
        using var exchanged = await ProviderApi.Exchange(Client, await TicketAsync("github", new FakeSignIn(FakePeople.GitHubNoPrimary)));

        await TenancyApi.AssertErrorAsync(exchanged, HttpStatusCode.Forbidden, "no_email");
    }

    [Fact]
    public async Task An_unverified_primary_address_is_next_link_and_never_a_finish()   // spec 0011, Settled 8
    {
        var ticket = await TicketAsync("github", new FakeSignIn(FakePeople.GitHubUnverified));

        using (var exchanged = await ProviderApi.Exchange(Client, ticket))
        {
            Assert.Equal("link", await NextOfAsync(exchanged));
        }

        using var finished = await ProviderApi.Finish(Client, ticket, "private");
        await TenancyApi.AssertErrorAsync(finished, HttpStatusCode.Forbidden, "email_not_verified");
    }

    [Fact]
    public async Task An_invitation_is_accepted_through_github_with_provider_github()   // spec 0011, criterion 20
    {
        var token = await InvitationTokenAsync(await CreateCompanyAsync("Acme"), Ola);

        using var accepted = await ProviderApi.Exchange(Client, await TicketAsync("github", GitHubPerson(), "?invite=" + token));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var row = await SingleAsync(AuditKinds.InviteAccepted);
        Assert.Equal("github", AuditApi.Text(row, "provider"));
        Assert.Equal(FakePeople.GitHubId, Assert.Single(await GitHubLoginsAsync()).ProviderKey);
    }

    [Fact]
    public async Task An_invitation_through_a_github_account_whose_primary_address_is_another_is_403_address_mismatch()   // Review Focus 4
    {
        // The invited address is the person's verified secondary entry at GitHub: only the primary entry counts.
        var token = await InvitationTokenAsync(await CreateCompanyAsync("Acme"), FakePeople.GitHubSecondaryEmail);

        using var refused = await ProviderApi.Exchange(Client, await TicketAsync("github", GitHubPerson(), "?invite=" + token));

        await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.Forbidden, "address_mismatch");
        Assert.Empty(await GitHubLoginsAsync());
    }

    [Fact]
    public async Task A_github_connect_and_unlink_run_as_for_the_other_providers()   // spec 0012, criteria 12 and 16 with GitHub
    {
        await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        using (var connected = await SettingsApi.Connect(Client, session, await TicketAsync("github", GitHubPerson(), "?purpose=connect")))
        {
            Assert.Equal(HttpStatusCode.NoContent, connected.StatusCode);
        }

        Assert.Equal(FakePeople.GitHubId, Assert.Single(await GitHubLoginsAsync()).ProviderKey);
        var linked = await SingleAsync(AuditKinds.ProviderLinked);
        Assert.Equal(("github", "settings"), (AuditApi.Text(linked, "provider"), AuditApi.Text(linked, "via")));

        using var unlinked = await SettingsApi.Unlink(Client, session, "github");

        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.Empty(await GitHubLoginsAsync());
        Assert.Equal("github", AuditApi.Text(await SingleAsync(AuditKinds.ProviderUnlinked), "provider"));
    }
}
