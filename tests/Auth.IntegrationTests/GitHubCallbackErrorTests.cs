using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012 → GitHub (criterion 5), one test per path: every failure of a GitHub round trip is one of the four codes of spec 0011, makes no ticket
/// and sets no <c>auth_pt</c>, and every token GitHub issued is revoked once. A failed revocation alone still makes the ticket.
/// </summary>
public sealed class GitHubCallbackErrorTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys, github: true)
{
    private const string UserRequest = "GET /user";
    private const string EmailsRequest = "GET /user/emails?per_page=100";

    private string[] _roundTripSecrets = [];

    /// <summary>The start and the fake's answer for this person; the callback is the caller's. Its state and code are what no log line may hold.</summary>
    private async Task<(ProviderApi.Start Start, string Back)> RoundTripAsync(FakeSignIn person, string query = "")
    {
        var start = await StartAsync("github", query);
        var back = await AuthorizeAsync(start, person);
        var code = QueryHelpers.ParseQuery(new Uri("https://app.example.com" + back).Query).TryGetValue("code", out var found) ? found.ToString() : "";
        _roundTripSecrets = [.. new[] { start.Query["state"], code }.Where(s => s.Length > 0)];
        return (start, back);
    }

    private async Task<HttpResponseMessage> CallbackOfAsync(FakeSignIn person)
    {
        var (start, back) = await RoundTripAsync(person);
        return await CallbackAsync(back, start.StateCookie);
    }

    /// <summary>
    /// The whole refusal: the redirect with exactly the code (and the purpose) in its fragment, no <c>auth_pt</c>, <c>no-store</c>, no ticket
    /// beyond <paramref name="tickets"/> (those of earlier good round trips), one Information line naming the provider and the code; and nothing
    /// GitHub or the round trip holds (its description of the refusal, a token it issued, the client secret and its Basic header, the state, the
    /// code, the verifier) in the redirect, the body or the log.
    /// </summary>
    private async Task AssertRefusedAsync(HttpResponseMessage callback, string code, string? purpose = "sign_in", int tickets = 0)
    {
        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        var location = callback.Headers.Location!.OriginalString;
        Assert.Equal(ProviderApi.ReturnUrl + "#error=" + code + (purpose is null ? "" : "&purpose=" + purpose), location);
        var body = await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(callback.Headers.CacheControl?.NoStore);
        Assert.Null(ProviderApi.BindingCookieOf(callback));
        Assert.Equal(tickets, await TicketCountAsync());
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains(code, StringComparison.Ordinal) && e.Message.Contains("github", StringComparison.Ordinal));

        var log = Logs.Text;
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(AuthAppFactory.GitHubClientId + ":" + AuthAppFactory.GitHubClientSecret));
        string[] held =
        [
            FakeOidcServer.RefusalDescription, AuthAppFactory.GitHubClientSecret, basic, .. Fake.Server.GitHub.IssuedTokens,
        ];
        foreach (var secret in held)
        {
            Assert.DoesNotContain(secret, location, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("code_verifier", log, StringComparison.Ordinal);
        foreach (var secret in _roundTripSecrets)
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }
    }

    /// <summary>GitHub issued one token, and it was revoked once (one attempt: Amendment A4).</summary>
    private void AssertRevokedOnce()
    {
        var token = Assert.Single(Fake.Server.GitHub.IssuedTokens);
        var revocation = Assert.Single(Fake.Server.GitHub.Revocations);
        Assert.Equal(token, revocation.Token);
        Assert.Equal(AuthAppFactory.GitHubClientId, revocation.ClientId);
        Assert.Equal(204, revocation.Status);   // GitHub took the revocation: its credentials and body were right
    }

    /// <summary>The read went as far as <paramref name="emails"/> says and no further, each document asked for once (no retry: Amendment A4).</summary>
    private void AssertReadOnce(bool emails)
    {
        Assert.Single(Fake.Server.GitHub.Requests, r => r == UserRequest);
        Assert.Equal(emails ? 1 : 0, Fake.Server.GitHub.Requests.Count(r => r == EmailsRequest));
    }

    /// <summary>
    /// Moves the test clock past the bound of a held call and awaits the callback. The bound is the clock's, so the answer comes at once; the
    /// HTTP client's own real-time timeout (ten seconds) would answer the same only far later, and must not be what ends the wait.
    /// </summary>
    private async Task<HttpResponseMessage> AdvancePastTheBoundAsync(Task<HttpResponseMessage> pending)
    {
        Clock.Advance(GitHubReader.CallBound + TimeSpan.FromSeconds(1));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var callback = await pending;
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The bound of the test clock did not end the call: it took {watch.Elapsed}.");
        return callback;
    }

    [Fact]
    public async Task Access_denied_with_a_valid_state_is_cancelled()   // criterion 5
    {
        using var callback = await CallbackOfAsync(new FakeSignIn(FakePeople.Cancel));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.Cancelled);
        Assert.Empty(Fake.Server.GitHub.IssuedTokens);
        Assert.Empty(Fake.Server.GitHub.Requests);   // GitHub was not asked for anything
        Assert.Empty(Fake.Server.GitHub.Revocations);
    }

    [Theory]
    [InlineData(FakePeople.GitHub)]
    [InlineData(FakePeople.Cancel)]
    public async Task An_answer_without_iss_is_provider_error_even_for_access_denied(string person)   // Amendment A6
    {
        using var callback = await CallbackOfAsync(new FakeSignIn(person, Defect: FakeDefects.NoIss));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        Assert.Empty(Fake.Server.GitHub.IssuedTokens);   // refused before the code is redeemed
        Assert.Empty(Fake.Server.GitHub.Requests);
    }

    [Theory]
    [InlineData("no state")]
    [InlineData("forged state")]
    [InlineData("expired state")]
    [InlineData("replayed state")]
    [InlineData("no cookie")]
    [InlineData("garbage cookie")]
    [InlineData("another browsers cookie")]
    public async Task A_missing_forged_expired_or_replayed_state_or_a_wrong_request_forgery_cookie_is_invalid_state(string how)   // criterion 5: as for the others
    {
        var (start, back) = await RoundTripAsync(GitHubPerson());
        var name = start.StateCookie[..start.StateCookie.IndexOf('=', StringComparison.Ordinal)];
        var path = back;
        var cookie = (string?)start.StateCookie;
        string? purpose = null;   // a state that cannot be read has no purpose; a cookie that is wrong belongs to a state that can be
        var earlier = 0;   // round trips that went through before the refused callback: each read and revoked once
        switch (how)
        {
            case "no state":
                path = "/auth/providers/github/callback?code=abc&iss=" + Uri.EscapeDataString(FakeGitHub.WebIssuer);
                break;
            case "forged state":
                path = back.Replace(start.Query["state"], "ZZZZZZZZZZ" + "ZZZZZZZZZZ" + "ZZZZZZZZZZ" + "ZZZZZZZZZZ" + "ZZZ", StringComparison.Ordinal);
                break;
            case "expired state":
                Clock.Advance(TimeSpan.FromMinutes(11));
                break;
            case "replayed state":
                using (var first = await CallbackAsync(back, start.StateCookie))
                {
                    ProviderApi.TicketOf(first);
                }

                earlier = 1;
                break;
            case "no cookie":
                cookie = null;
                purpose = "sign_in";
                break;
            case "garbage cookie":
                cookie = name + "=garbage";
                purpose = "sign_in";
                break;
            default:
                cookie = name + "=" + (await StartAsync("github")).StateCookieHeader.Value.Value;
                purpose = "sign_in";
                break;
        }

        using var callback = await CallbackAsync(path, cookie);

        await AssertRefusedAsync(callback, ProviderCallbackErrors.InvalidState, purpose, tickets: earlier);
        Assert.Equal(earlier, Fake.Server.GitHub.IssuedTokens.Count);   // the refused callback redeemed nothing, read nothing, revoked nothing
        Assert.Equal(earlier, Fake.Server.GitHub.Revocations.Count);
        Assert.Equal(earlier, Fake.Server.GitHub.Requests.Count(r => r == "GET /user"));
        Assert.Equal(earlier, Fake.Server.GitHub.Requests.Count(r => r.StartsWith("POST", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_refused_code_is_provider_error_and_no_token_exists_to_revoke()   // criterion 5
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.RefuseCode));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        Assert.Empty(Fake.Server.GitHub.IssuedTokens);
        Assert.Equal(["POST /login/oauth/access_token"], Fake.Server.GitHub.Requests);   // the code was tried once; no read, nothing to revoke
        Assert.Empty(Fake.Server.GitHub.Revocations);
    }

    [Fact]
    public async Task A_token_answer_without_an_access_token_is_provider_error()   // Amendment A3
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.NoToken));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        Assert.DoesNotContain(Fake.Server.GitHub.Requests, r => r.StartsWith("GET", StringComparison.Ordinal));
        Assert.Empty(Fake.Server.GitHub.Revocations);   // there was no token to revoke
    }

    [Fact]
    public async Task A_provider_error_at_github_is_provider_error()   // criterion 5
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.ProviderError));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        Assert.Empty(Fake.Server.GitHub.IssuedTokens);
        Assert.Empty(Fake.Server.GitHub.Requests);
        Assert.Empty(Fake.Server.GitHub.Revocations);
    }

    [Fact]
    public async Task A_user_answer_that_is_not_200_is_provider_error_after_one_attempt_and_the_token_is_revoked()   // criterion 5, Review Focus 3
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.UserFails));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertReadOnce(emails: false);   // no retry
        AssertRevokedOnce();
    }

    [Fact]
    public async Task A_user_answer_that_is_not_json_is_provider_error_and_the_token_is_revoked()   // criterion 5
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.UserNotJson));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertReadOnce(emails: false);
        AssertRevokedOnce();
    }

    [Theory]
    [InlineData(FakeDefects.UserNoId)]
    [InlineData(FakeDefects.UserBadId)]
    public async Task A_user_without_an_id_or_with_one_that_is_no_number_is_provider_error_and_the_token_is_revoked(string defect)   // criterion 5
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: defect));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertReadOnce(emails: true);   // both documents are read before the id is judged
        AssertRevokedOnce();
    }

    [Fact]
    public async Task A_user_answer_larger_than_64_kib_is_provider_error_and_the_token_is_revoked()   // spec 0012, step 3
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.UserTooLarge));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertReadOnce(emails: false);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task A_user_answer_slower_than_ten_seconds_is_provider_error_and_the_token_is_revoked()   // spec 0012, step 3; Review Focus 3
    {
        var (start, back) = await RoundTripAsync(GitHubPerson(defect: FakeDefects.UserSlow));

        var pending = CallbackAsync(back, start.StateCookie);
        await Fake.Server.GitHub.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var callback = await AdvancePastTheBoundAsync(pending);

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertReadOnce(emails: false);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task An_emails_answer_that_is_not_200_is_provider_error_after_one_attempt_and_the_token_is_revoked()   // criterion 5
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.EmailsFails));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertReadOnce(emails: true);
        AssertRevokedOnce();
    }

    [Theory]
    [InlineData(FakeDefects.EmailsNotJson)]
    [InlineData(FakeDefects.EmailsBadEntry)]
    public async Task An_emails_answer_that_is_not_the_documented_json_is_provider_error_and_the_token_is_revoked(string defect)   // criterion 5
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: defect));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertReadOnce(emails: true);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task An_emails_answer_larger_than_64_kib_is_provider_error_and_the_token_is_revoked()   // spec 0012, step 3: the bound of the second call
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.EmailsTooLarge));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertReadOnce(emails: true);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task An_emails_answer_slower_than_ten_seconds_is_provider_error_and_the_token_is_revoked()   // spec 0012, step 3: the bound of the second call
    {
        var (start, back) = await RoundTripAsync(GitHubPerson(defect: FakeDefects.EmailsSlow));

        var pending = CallbackAsync(back, start.StateCookie);
        await Fake.Server.GitHub.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var callback = await AdvancePastTheBoundAsync(pending);

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertReadOnce(emails: true);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task A_failed_revocation_still_makes_the_ticket_and_logs_one_warning_without_the_token()   // criterion 5, Settled 20
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.RevokeFails));

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
        Assert.Equal(500, Assert.Single(Fake.Server.GitHub.Revocations).Status);   // tried once
        var warning = Assert.Single(Logs.Entries, e => e.Level == LogLevel.Warning && e.Category == typeof(GitHubReader).FullName);
        Assert.Contains("github", warning.Message, StringComparison.Ordinal);
        Assert.Contains("500", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Assert.Single(Fake.Server.GitHub.IssuedTokens), Logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revocation_slower_than_ten_seconds_still_makes_the_ticket_and_logs_one_warning_naming_the_timeout()   // spec 0012, step 4: the revocation's own bound
    {
        var (start, back) = await RoundTripAsync(GitHubPerson(defect: FakeDefects.RevokeSlow));

        var pending = CallbackAsync(back, start.StateCookie);
        await Fake.Server.GitHub.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var callback = await AdvancePastTheBoundAsync(pending);

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
        Assert.Equal(0, Assert.Single(Fake.Server.GitHub.Revocations).Status);   // tried once, never answered
        var warning = Assert.Single(Logs.Entries, e => e.Level == LogLevel.Warning && e.Category == typeof(GitHubReader).FullName);
        Assert.Contains("github", warning.Message, StringComparison.Ordinal);
        Assert.Contains("timeout", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Assert.Single(Fake.Server.GitHub.IssuedTokens), Logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_dropped_from_the_manifest_during_the_round_trip_is_provider_unavailable_and_the_token_is_revoked()   // spec 0011's rule
    {
        var (start, back) = await RoundTripAsync(GitHubPerson());
        SetManifest(AuthAppFactory.SelfServiceManifest + AuthAppFactory.PrivateBlock + AuthAppFactory.ProvidersLine, withProviders: false);

        using var callback = await CallbackAsync(back, start.StateCookie);

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderUnavailable);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task No_refusal_writes_an_audit_row()   // spec 0011: a failed callback writes no row
    {
        var before = (await AuditAsync()).Count;

        using (var refused = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.UserFails)))
        {
            await AssertRefusedAsync(refused, ProviderCallbackErrors.ProviderError);
        }

        Assert.Equal(before, (await AuditAsync()).Count);
        Assert.Equal(0, await InDbAsync(db => db.ProviderTickets.CountAsync(TestContext.Current.CancellationToken)));
    }
}
