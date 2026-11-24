using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Client.AspNetCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → GitHub, steps 2 to 4 (criteria 3 and 7): the callback reads, revokes, and makes the ticket of the identity.</summary>
public sealed class GitHubCallbackTests : ProviderTestBase
{
    /// <summary>The authentication properties of every outcome of the OpenIddict client the callback endpoint received; the host is built before a test body runs, so the recorder is registered here.</summary>
    private readonly List<IDictionary<string, string?>> _seen = [];

    public GitHubCallbackTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys, github: true)
    {
        Factory.WithServices(services => services.AddScoped<IAuthenticationService>(
            provider => ActivatorUtilities.CreateInstance<RecordedAuthentication>(provider, _seen)));
    }

    private static string RevocationRequest => $"DELETE /applications/{AuthAppFactory.GitHubClientId}/token";

    [Fact]
    public async Task A_good_code_reads_user_and_emails_revokes_the_token_and_makes_a_ticket_of_the_primary_address()   // criterion 3
    {
        var ticket = await TicketAsync("github", GitHubPerson());

        Assert.Equal("sign_in", ticket.Purpose);
        Assert.Equal(43, ticket.Value.Length);
        var row = await InDbAsync(db => db.ProviderTickets.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("github", FakePeople.GitHubId, FakePeople.GitHubEmail, true), (row.Provider, row.IdentityKey, row.Email, row.EmailVerified));
        Assert.Equal(
            ["POST /login/oauth/access_token", "GET /user", "GET /user/emails?per_page=100", RevocationRequest],
            Fake.Server.GitHub.Requests);
        var token = Assert.Single(Fake.Server.GitHub.IssuedTokens);
        Assert.Equal(new GitHubRevocation(AuthAppFactory.GitHubClientId, token, 204), Assert.Single(Fake.Server.GitHub.Revocations));
    }

    [Theory]
    [InlineData(FakePeople.GitHubUnverified, FakePeople.GitHubUnverifiedId, FakePeople.GitHubUnverifiedEmail, false)]
    [InlineData(FakePeople.GitHubNoPrimary, FakePeople.GitHubNoPrimaryId, null, false)]
    public async Task An_unverified_primary_address_and_none_come_back_as_such(string person, string key, string? email, bool verified)   // criterion 3
    {
        await TicketAsync("github", new FakeSignIn(person));

        var row = await InDbAsync(db => db.ProviderTickets.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal((key, email, verified), (row.IdentityKey, row.Email, row.EmailVerified));
    }

    [Fact]
    public async Task A_free_address_is_one_github_identity_derived_from_it()   // criterion 32: the same address, the same identity, every time
    {
        await TicketAsync("github", new FakeSignIn(FakePeople.Custom, "anna@firma.test", Verified: true));
        await TicketAsync("github", new FakeSignIn(FakePeople.Custom, "anna@firma.test", Verified: true));

        var keys = await InDbAsync(db => db.ProviderTickets.AsNoTracking().Select(t => t.IdentityKey).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([FakePeople.GitHubIdOf("anna@firma.test"), FakePeople.GitHubIdOf("anna@firma.test")], keys);
    }

    [Fact]
    public async Task An_invitation_and_a_reauth_come_back_with_their_purpose_and_the_reauth_ticket_lives_five_minutes()   // criterion 3
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, FakePeople.GitHubEmail, "user");
        await EnqueueInvitationAsync(invite, FakePeople.GitHubEmail);
        await DispatchAsync();
        var token = TokenIn(Mail.Sent.Last(m => m.To == FakePeople.GitHubEmail));

        Assert.Equal("invite", (await TicketAsync("github", GitHubPerson(), "?invite=" + token)).Purpose);
        Assert.Equal("reauth", (await TicketAsync("github", GitHubPerson(), "?purpose=reauth")).Purpose);
        var reauth = await InDbAsync(db => db.ProviderTickets.AsNoTracking().SingleAsync(t => t.Purpose == Auth.Infrastructure.Persistence.ProviderTicketPurpose.Reauth, TestContext.Current.CancellationToken));
        Assert.Equal(Auth.Server.Email.StorableTime.Now(Clock) + TimeSpan.FromMinutes(5), reauth.ExpiresAt);
        Assert.Equal(2, Fake.Server.GitHub.Revocations.Count);
    }

    [Fact]
    public async Task No_code_token_state_ticket_or_client_secret_reaches_the_log()   // criterion 7
    {
        var start = await StartAsync("github");
        var back = await AuthorizeAsync(start, GitHubPerson());

        using var callback = await CallbackAsync(back, start.StateCookie);

        var ticket = ProviderApi.TicketOf(callback);
        var code = QueryHelpers.ParseQuery(new Uri("https://app.example.com" + back).Query)["code"].ToString();
        var text = Logs.Text;
        Assert.DoesNotContain(code, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Assert.Single(Fake.Server.GitHub.IssuedTokens), text, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthAppFactory.GitHubClientSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(AuthAppFactory.GitHubClientId + ":" + AuthAppFactory.GitHubClientSecret)),
            text, StringComparison.Ordinal);   // the Basic header of the revocation
        Assert.DoesNotContain(start.Query["state"], text, StringComparison.Ordinal);
        Assert.DoesNotContain("code_verifier", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket.Value, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket.Binding, text, StringComparison.Ordinal);
        Assert.Contains(Logs.Entries, e => e.Message.Contains("github", StringComparison.Ordinal) && e.Message.Contains("sign_in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenIddict_reads_no_userinfo_and_the_reader_reads_each_document_once()   // spec 0012, step 2: userinfo stays off
    {
        await TicketAsync("github", GitHubPerson());

        Assert.Single(Fake.Server.GitHub.Requests, r => r == "GET /user");
        Assert.Single(Fake.Server.GitHub.Requests, r => r == "GET /user/emails?per_page=100");
        Assert.Equal(0, Fake.Server.UserInfoRequests);
    }

    [Fact]
    public async Task A_callback_that_reaches_the_service_under_another_host_still_makes_a_ticket()   // Amendment A2 of spec 0011, for GitHub
    {
        var start = await StartAsync("github");
        var back = await AuthorizeAsync(start, GitHubPerson());

        using var callback = await ProviderApi.CallbackAsync(Browser, back, start.StateCookie, request => request.Headers.Host = "auth-core:8080");

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(FakeDefects.UserFails, false)]
    public async Task The_callback_never_sees_the_github_token_after_a_good_read_or_a_failed_one(string? defect, bool good)   // Amendment A3, criterion 7
    {
        var start = await StartAsync("github");
        var back = await AuthorizeAsync(start, GitHubPerson(defect: defect));

        using var callback = await CallbackAsync(back, start.StateCookie);

        var token = Assert.Single(Fake.Server.GitHub.IssuedTokens);
        var items = Assert.Single(_seen);   // the outcome of the client's authentication, as the callback reads it
        Assert.DoesNotContain(items.Keys, key => key.Contains("access_token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items.Values, value => value is not null && value.Contains(token, StringComparison.Ordinal));
        Assert.Equal(good, items.ContainsKey(GitHubItems.Key));   // not vacuous: the identity is there on a good read, and only then
    }

    /// <summary>Keeps the authentication properties of the OpenIddict client's outcome, which is what the callback endpoint receives.</summary>
    private sealed class RecordedAuthentication(
        IAuthenticationSchemeProvider schemes, IAuthenticationHandlerProvider handlers, IClaimsTransformation transformation,
        IOptions<AuthenticationOptions> options, List<IDictionary<string, string?>> seen)
        : AuthenticationService(schemes, handlers, transformation, options)
    {
        public override async Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            var result = await base.AuthenticateAsync(context, scheme);
            if (string.Equals(scheme, OpenIddictClientAspNetCoreDefaults.AuthenticationScheme, StringComparison.Ordinal) && result.Properties is { } properties)
            {
                seen.Add(new Dictionary<string, string?>(properties.Items, StringComparer.Ordinal));
            }

            return result;
        }
    }
}
