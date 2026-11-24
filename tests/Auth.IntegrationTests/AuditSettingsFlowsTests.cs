using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012, Audit events (criteria 25 and 7): every new kind and every new <c>details</c> member, written through the endpoints, with its
/// fields, and no row holds a password, a ticket, a cookie, a token, or the token or the client secret of GitHub.
/// </summary>
public sealed class AuditSettingsFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys, github: true)
{
    private const string Pat = "pat@firma.test";
    private const string PatPassword = UserPassword;
    private const string PatNewPassword = "Fresh-Passw0rd-2";

    private readonly List<string> _secrets = [PatPassword, PatNewPassword, "Wrong-Passw0rd-9", AuthAppFactory.GitHubClientSecret];

    private async Task<ProviderApi.Ticket> TrackedTicketAsync(string provider, FakeSignIn person, string query = "")
    {
        var ticket = await TicketAsync(provider, person, query);
        _secrets.Add(ticket.Value);
        _secrets.Add(ticket.Binding);
        return ticket;
    }

    private SessionApi.Session Track(SessionApi.Session session)
    {
        _secrets.Add(session.AccessToken);
        _secrets.Add(session.RefreshToken);
        return session;
    }

    private async Task<SessionApi.Session> SessionOfAsync(HttpResponseMessage response)
    {
        using (response)
        {
            return Track(await SessionApi.ReadSessionAsync(response));
        }
    }

    private static async Task ExpectAsync(Task<HttpResponseMessage> sent, HttpStatusCode status)
    {
        using var response = await sent;
        Assert.Equal(status, response.StatusCode);
    }

    private static void AssertOwnRow(AuditEvent row, Guid userId, string email)
    {
        Assert.Equal((userId, userId, email), (row.ActorUserId!.Value, row.SubjectUserId!.Value, row.SubjectEmail));
        Assert.NotNull(row.OrgId);
    }

    [Fact]
    public void The_four_new_kinds_are_in_the_contract()   // criterion 25
    {
        Assert.Equal(33, AuditKinds.All.Count);
        Assert.Contains(AuditKinds.AccountConfirmed, AuditKinds.All);
        Assert.Contains(AuditKinds.AccountConfirmRefused, AuditKinds.All);
        Assert.Contains(AuditKinds.ProviderUnlinked, AuditKinds.All);
        Assert.Contains(AuditKinds.PasswordChanged, AuditKinds.All);
    }

    [Fact]
    public async Task Every_row_of_the_settings_flows_holds_its_fields_and_no_secret()   // criteria 25 and 7
    {
        // A GitHub sign-up: signup.completed and login.succeeded name github.
        var gitHub = await TrackedTicketAsync("github", GitHubPerson());
        var ola = await SessionOfAsync(await ProviderApi.Finish(Client, gitHub, "private"));
        var olaId = Guid.Parse(AccessTokens.Text(ola.AccessToken, "sub"));

        // The confirmation with GitHub (account.confirmed, method github), a refused one (reauth_failed), the connect of Google (via settings),
        // the unlink of GitHub (provider.unlinked, and login.succeeded via session_renewal).
        Clock.Advance(TimeSpan.FromMinutes(6));
        ola = Track(await SessionApi.RefreshOk(Client, ola.RefreshToken));
        await ExpectAsync(SettingsApi.Post(Client, SettingsApi.ConfirmPath, ola, new { provider_ticket = "nope" }), HttpStatusCode.Forbidden);
        await ExpectAsync(SettingsApi.ConfirmWithTicket(Client, ola, await TrackedTicketAsync("github", GitHubPerson(), "?purpose=reauth")), HttpStatusCode.NoContent);
        await ExpectAsync(
            SettingsApi.Connect(Client, ola, await TrackedTicketAsync("google", GooglePerson(email: FakePeople.GitHubEmail, subject: "100000000000000000088"), "?purpose=connect")),
            HttpStatusCode.NoContent);
        ola = await SessionOfAsync(await SettingsApi.Unlink(Client, ola, "github"));

        // A password account: a wrong and a right confirmation, a wrong and a right password change.
        var pat = await CreateUserAsync(Pat, confirmed: true);
        var patSession = Track(await SessionApi.LoginAsync(Client, Pat, PatPassword));
        Clock.Advance(LockoutApi.HumanPace);
        await ExpectAsync(SettingsApi.Confirm(Client, patSession, new { password = "Wrong-Passw0rd-9" }), HttpStatusCode.Forbidden);
        Clock.Advance(LockoutApi.HumanPace);
        await ExpectAsync(SettingsApi.Confirm(Client, patSession, new { password = PatPassword }), HttpStatusCode.NoContent);
        Clock.Advance(LockoutApi.HumanPace);
        await ExpectAsync(SettingsApi.ChangePassword(Client, patSession, "Wrong-Passw0rd-9", PatNewPassword), HttpStatusCode.Forbidden);
        Clock.Advance(LockoutApi.HumanPace);
        await SessionOfAsync(await SettingsApi.ChangePassword(Client, patSession, PatPassword, PatNewPassword));

        // The link of spec 0011 (via sign_in), an invitation through GitHub, and a company deleted with a ticket after a refused one.
        await CreateUserAsync("anna@firma.test", confirmed: true);
        await SessionOfAsync(await ProviderApi.Link(
            Client, await TrackedTicketAsync("google", GooglePerson(email: "anna@firma.test", subject: "100000000000000000033")), UserPassword));
        var company = await CreateCompanyAsync("Acme");
        var inviteToken = await InvitationTokenAsync(company, "jan@firma.test");
        _secrets.Add(inviteToken);
        await SessionOfAsync(await ProviderApi.Exchange(
            Client, await TrackedTicketAsync("github", GitHubPerson(email: "jan@firma.test", subject: "70000077"), "?invite=" + inviteToken)));
        var (beta, bossId, bossToken) = await CompanyWithAdminAsync("Beta", "boss@beta.test");
        _secrets.Add(bossToken);
        await InDbAsync(db => db.Users.Where(u => u.Id == bossId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
        await LinkAsync(bossId, "github", "70000055");
        await ExpectAsync(OrgApi.Delete(Client, bossToken, new { name = "Beta", provider_ticket = "nope" }), HttpStatusCode.Forbidden);
        await ExpectAsync(
            OrgApi.DeleteWithTicket(Client, bossToken, "Beta", await TrackedTicketAsync("github", GitHubPerson(email: "boss@beta.test", subject: "70000055"), "?purpose=reauth")),
            HttpStatusCode.NoContent);

        // The fields of each new row and each new member.
        var confirmed = await AuditAsync(AuditKinds.AccountConfirmed);
        Assert.Equal(2, confirmed.Count);
        Assert.All(confirmed, row => Assert.Equal(["method"], AuditApi.DetailNames(row)));
        Assert.Equal(["github", "password"], confirmed.Select(r => AuditApi.Text(r, "method")!).Order(StringComparer.Ordinal));
        AssertOwnRow(confirmed.Single(r => AuditApi.Text(r, "method") == "github"), olaId, FakePeople.GitHubEmail);
        AssertOwnRow(confirmed.Single(r => AuditApi.Text(r, "method") == "password"), pat.Id, Pat);

        var refused = await AuditAsync(AuditKinds.AccountConfirmRefused);
        Assert.All(refused, row => Assert.Equal(["action", "reason"], AuditApi.DetailNames(row)));
        Assert.Equal(
            ["confirm:reauth_failed", "confirm:wrong_password", "password:wrong_password"],
            refused.Select(r => AuditApi.Text(r, "action") + ":" + AuditApi.Text(r, "reason")).Order(StringComparer.Ordinal));

        var unlinked = await SingleAsync(AuditKinds.ProviderUnlinked);
        Assert.Equal(["provider"], AuditApi.DetailNames(unlinked));
        Assert.Equal("github", AuditApi.Text(unlinked, "provider"));
        AssertOwnRow(unlinked, olaId, FakePeople.GitHubEmail);

        var changed = await SingleAsync(AuditKinds.PasswordChanged);
        Assert.Empty(AuditApi.DetailNames(changed));
        AssertOwnRow(changed, pat.Id, Pat);

        var linked = await AuditAsync(AuditKinds.ProviderLinked);
        Assert.All(linked, row => Assert.Equal(["provider", "via"], AuditApi.DetailNames(row)));
        Assert.Equal(["google:settings", "google:sign_in"], linked.Select(r => AuditApi.Text(r, "provider") + ":" + AuditApi.Text(r, "via")).Order(StringComparer.Ordinal));

        var logins = await AuditAsync(AuditKinds.LoginSucceeded);
        var renewals = logins.Where(r => AuditApi.Text(r, "via") == "session_renewal").ToList();
        Assert.Equal(2, renewals.Count);
        Assert.All(renewals, row => Assert.Equal(["via"], AuditApi.DetailNames(row)));
        Assert.Contains(logins, r => AuditApi.Text(r, "provider") == "github");
        Assert.Equal("github", AuditApi.Text(await SingleAsync(AuditKinds.SignupCompleted), "provider"));
        Assert.Equal("github", AuditApi.Text(await SingleAsync(AuditKinds.InviteAccepted), "provider"));

        var reauthenticated = await SingleAsync(AuditKinds.ProviderReauthenticated);
        Assert.Equal(("github", beta), (AuditApi.Text(reauthenticated, "provider"), reauthenticated.OrgId!.Value));
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.OrgDeleteRefused), "reason"));

        // No row holds a secret of these flows.
        _secrets.AddRange(Fake.Server.GitHub.IssuedTokens);
        Assert.NotEmpty(Fake.Server.GitHub.IssuedTokens);
        await AssertNoSecretsAsync([.. _secrets]);
    }
}
