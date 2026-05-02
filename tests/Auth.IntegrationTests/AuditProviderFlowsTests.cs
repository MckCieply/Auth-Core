using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.WebUtilities;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0011, criterion 28: the audit log knows 28 kinds; every row and every <c>details</c> member the provider flows add holds the fields of the
/// contract; no row holds a code, a state, an identity token, a ticket, a cookie or a password; a failed callback writes no row.
/// </summary>
public sealed class AuditProviderFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private readonly List<string> _secrets = [];

    /// <summary>A round trip that keeps every secret it saw: the state, the challenge, the code, the request-forgery cookie, the ticket, its binding.</summary>
    private async Task<ProviderApi.Ticket> TrackedTicketAsync(string provider, FakeSignIn person, string query = "")
    {
        var start = await StartAsync(provider, query);
        var back = await AuthorizeAsync(start, person);
        using var callback = await CallbackAsync(back, start.StateCookie);
        var ticket = ProviderApi.TicketOf(callback);
        var answer = QueryHelpers.ParseQuery(new Uri("https://app.example.com" + back).Query);
        _secrets.AddRange(
        [
            start.Query["state"], start.Query["code_challenge"], start.Query["nonce"], answer["code"].ToString(),
            start.StateCookieHeader.Value.Value!, ticket.Value, ticket.Binding,
        ]);
        return ticket;
    }

    private static bool Names(AuditEvent row, params string[] names) => AuditApi.DetailNames(row).SequenceEqual(names);

    [Fact]
    public void The_audit_log_knows_twenty_eight_kinds()   // criterion 28
    {
        Assert.Equal(28, AuditKinds.All.Count);
        Assert.Contains("provider.linked", AuditKinds.All);
        Assert.Contains("provider.reauthenticated", AuditKinds.All);
    }

    [Fact]
    public async Task Every_provider_flow_writes_the_rows_of_the_contract_and_no_secret()   // criterion 28
    {
        // A link, after a wrong password, and then a sign-in of the linked identity.
        var anna = await CreateUserAsync("anna@firma.test", confirmed: true);
        var linkTicket = await TrackedTicketAsync("google", GooglePerson(email: "anna@firma.test", subject: "800000000000000000008"));
        using (var wrong = await ProviderApi.Link(Client, linkTicket, WrongPassword))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using (var linked = await ProviderApi.Link(Client, linkTicket, UserPassword))
        {
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        }

        using (var signedIn = await ProviderApi.Exchange(Client, await TrackedTicketAsync("google", GooglePerson(email: "anna@firma.test", subject: "800000000000000000008"))))
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        }

        // A private finish, then a refused and an accepted re-authentication of its deletion.
        var finishTicket = await TrackedTicketAsync("microsoft", PersonalMicrosoft());
        using var finished = await ProviderApi.Finish(Client, finishTicket, "private");
        var ola = await SessionApi.ReadSessionAsync(finished);
        _secrets.AddRange([ola.AccessToken, ola.RefreshToken]);
        var signInTicket = await TrackedTicketAsync("microsoft", PersonalMicrosoft());
        using (var refused = await ProviderApi.DeleteAccount(Client, ola.AccessToken, new { provider_ticket = signInTicket.Value }, signInTicket.Binding))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        var reauth = await TrackedTicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");
        using (var deleted = await ProviderApi.DeleteAccount(Client, ola.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        // An invitation accepted with Google.
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "jan@firma.test", "user");
        await EnqueueInvitationAsync(invite, "jan@firma.test");
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == "jan@firma.test"));
        _secrets.Add(inviteToken);
        using (var accepted = await ProviderApi.Exchange(Client, await TrackedTicketAsync("google", GooglePerson(email: "jan@firma.test", subject: "900000000000000000009"), "?invite=" + inviteToken)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        // A round trip that fails at the callback writes no row.
        var before = (await AuditAsync()).Count;
        var cancelled = await StartAsync("google");
        var cancelledBack = await AuthorizeAsync(cancelled, new FakeSignIn(FakePeople.Cancel));
        _secrets.AddRange(
        [
            cancelled.Query["state"], cancelled.Query["code_challenge"], cancelled.Query["nonce"], cancelled.StateCookieHeader.Value.Value!,
        ]);
        using (var callback = await CallbackAsync(cancelledBack, cancelled.StateCookie))
        {
            Assert.Equal("cancelled", ProviderApi.FragmentOf(callback)["error"]);
        }

        Assert.Equal(before, (await AuditAsync()).Count);

        // The rows.
        var providerLogins = (await AuditAsync(AuditKinds.LoginSucceeded)).Where(r => AuditApi.Text(r, "provider") is not null).ToList();
        Assert.Equal(4, providerLogins.Count);   // the link, the sign-in, the finish, the invitation
        Assert.All(providerLogins, row => Assert.True(Names(row, "provider")));

        var failed = Assert.Single(await AuditAsync(AuditKinds.LoginFailed));
        Assert.True(Names(failed, "provider", "reason"));
        Assert.Equal(anna.Id, failed.SubjectUserId);

        var link = await SingleAsync(AuditKinds.ProviderLinked);
        Assert.Equal((anna.Id, anna.Id, "anna@firma.test"), (link.ActorUserId!.Value, link.SubjectUserId!.Value, link.SubjectEmail));
        Assert.NotNull(link.OrgId);
        Assert.NotNull(link.OrgName);
        Assert.True(Names(link, "provider"));

        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.True(Names(completed, "kind", "provider"));
        Assert.Equal("microsoft", AuditApi.Text(completed, "provider"));

        var refusal = await SingleAsync(AuditKinds.AccountDeleteRefused);
        Assert.Equal("reauth_failed", AuditApi.Text(refusal, "reason"));
        Assert.True(Names(refusal, "reason"));

        var reauthenticated = await SingleAsync(AuditKinds.ProviderReauthenticated);
        Assert.True(Names(reauthenticated, "provider"));
        Assert.Equal(reauthenticated.SubjectUserId, reauthenticated.ActorUserId);
        Assert.Equal(FakePeople.PersonalEmail, reauthenticated.SubjectEmail);
        Assert.NotNull(reauthenticated.OrgId);
        Assert.Equal(PersonalSpaces.Name, reauthenticated.OrgName);
        var deletion = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal("self", AuditApi.Text(deletion, "reason"));
        Assert.True(Names(deletion, "reason"));

        var acceptance = await SingleAsync(AuditKinds.InviteAccepted);
        Assert.True(Names(acceptance, "provider", "role"));
        Assert.Equal(company, acceptance.OrgId);

        // Every row is of a kind of the contract; besides the kinds read above only the set-up of the test (the company, the mail of the invitation)
        // wrote any: no flow leaves a stray row.
        var written = (await AuditAsync()).Select(r => r.Kind).Distinct(StringComparer.Ordinal).ToList();
        Assert.All(written, kind => Assert.Contains(kind, AuditKinds.All));
        string[] read =
        [
            AuditKinds.AccountDeleteRefused, AuditKinds.AccountDeleted, AuditKinds.InviteAccepted, AuditKinds.LoginFailed,
            AuditKinds.LoginSucceeded, AuditKinds.ProviderLinked, AuditKinds.ProviderReauthenticated, AuditKinds.SignupCompleted,
        ];
        Assert.All(read, kind => Assert.Contains(kind, written));
        Assert.Empty(written.Except(read).Except([AuditKinds.OrgCreated, AuditKinds.InviteSent]));

        _secrets.AddRange(Fake.Server.IssuedIdentityTokens);
        string[] secrets = [.. _secrets, UserPassword, WrongPassword];
        await AssertNoSecretsAsync(secrets);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal);
        }
    }
}
