using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012, criterion 32: the fake provider as GitHub, by host and under the test issuer's <c>/github</c>.</summary>
public sealed class FakeGitHubTests : IAsyncDisposable
{
    private const string Verifier = "a-verifier-of-at-least-forty-three-characters-0123456789";
    private const string ClientId = "the-github-client";
    private const string Callback = "https://app.example.com/auth/providers/github/callback";
    private const string Authorize = "https://github.com/login/oauth/authorize";
    private const string TokenEndpoint = "https://github.com/login/oauth/access_token";
    private const string Api = "https://api.github.com";

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeOidcHost _fake;

    public FakeGitHubTests()
    {
        _fake = new FakeOidcHost(_clock);
    }

    private FakeGitHub GitHub => _fake.Server.GitHub;

    public async ValueTask DisposeAsync()
    {
        await _fake.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static string Challenge(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string AuthorizeUrl(string endpoint = Authorize, string scope = "user:email", string? method = "S256") =>
        QueryHelpers.AddQueryString(endpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = Callback,
            ["scope"] = scope,
            ["state"] = "the-state",
            ["code_challenge"] = Challenge(Verifier),
            ["code_challenge_method"] = method,
        });

    /// <summary>The query of the fake's redirect back to the callback.</summary>
    private async Task<Dictionary<string, string>> AuthorizeAsync(FakeSignIn person, string endpoint = Authorize)
    {
        using var response = await _fake.Client.GetAsync(person.AppendTo(AuthorizeUrl(endpoint)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(Callback + "?", location.AbsoluteUri, StringComparison.Ordinal);
        return QueryHelpers.ParseQuery(location.Query).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
    }

    private async Task<HttpResponseMessage> RedeemAsync(
        string code, string verifier = Verifier, bool json = true, string endpoint = TokenEndpoint, string clientId = ClientId, string redirect = Callback)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirect,
                ["client_id"] = clientId,
                ["client_secret"] = "any-secret",
                ["code_verifier"] = verifier,
            }),
        };
        if (json)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        return await _fake.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>A whole round trip at the fake: the access token GitHub would give for this person.</summary>
    private async Task<string> AccessTokenAsync(FakeSignIn person)
    {
        using var redeemed = await RedeemAsync((await AuthorizeAsync(person))["code"]);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        return JsonNode.Parse(await redeemed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["access_token"]!.GetValue<string>();
    }

    private async Task<HttpResponseMessage> ApiAsync(string path, string? token, bool asGitHubAsks = true, string origin = Api, CancellationToken? cancel = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, origin + path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (asGitHubAsks)
        {
            request.Headers.UserAgent.ParseAdd("Auth-Core");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
        }

        return await _fake.Client.SendAsync(request, cancel ?? TestContext.Current.CancellationToken);
    }

    private static async Task<JsonNode> JsonOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<HttpResponseMessage> RevokeAsync(string token, string clientId = ClientId, string? basicId = ClientId, string origin = Api, CancellationToken? cancel = null,
        bool userAgent = true, bool accept = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{origin}/applications/{clientId}/token")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { access_token = token }), Encoding.UTF8, "application/json"),
        };
        if (basicId is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basicId + ":any-secret")));
        }

        if (userAgent)
        {
            request.Headers.UserAgent.ParseAdd("Auth-Core");
        }

        if (accept)
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
        }

        return await _fake.Client.SendAsync(request, cancel ?? TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_authorization_answers_at_once_with_a_code_the_state_and_githubs_iss()   // criterion 32, Amendment A6
    {
        var answer = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub));

        Assert.Equal(32, answer["code"].Length);
        Assert.Equal("the-state", answer["state"]);
        Assert.Equal(FakeGitHub.WebIssuer, answer["iss"]);
    }

    [Fact]
    public async Task The_test_issuer_answers_without_iss_and_the_no_iss_defect_leaves_it_out_at_github()   // Amendment A6
    {
        var issuer = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub), "http://issuer.test/github/authorize");
        var withoutIss = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.NoIss));

        Assert.False(issuer.ContainsKey("iss"));
        Assert.False(withoutIss.ContainsKey("iss"));
        Assert.True(withoutIss.ContainsKey("code"));
    }

    [Theory]
    [InlineData(FakePeople.Cancel, null, "access_denied")]
    [InlineData(FakePeople.GitHub, FakeDefects.ProviderError, "server_error")]
    public async Task Cancel_and_the_provider_error_defect_answer_their_error_with_the_state_and_iss(string person, string? defect, string error)   // criterion 32
    {
        var answer = await AuthorizeAsync(new FakeSignIn(person, Defect: defect));

        Assert.Equal(error, answer["error"]);
        Assert.Equal(FakeOidcServer.RefusalDescription, answer["error_description"]);
        Assert.Equal("the-state", answer["state"]);
        Assert.Equal(FakeGitHub.WebIssuer, answer["iss"]);
        Assert.False(answer.ContainsKey("code"));
    }

    [Theory]
    [InlineData("openid email profile", "S256")]
    [InlineData("user:email", null)]
    [InlineData("user:email", "plain")]
    public async Task An_authorization_request_without_pkce_s256_or_the_scope_user_email_is_refused(string scope, string? method)   // spec 0012 → start
    {
        using var response = await _fake.Client.GetAsync(
            new FakeSignIn(FakePeople.GitHub).AppendTo(AuthorizeUrl(scope: scope, method: method)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_person_the_page_offers_the_three_people_a_free_address_and_cancel()   // criterion 32: what a browser sees
    {
        using var response = await _fake.Client.GetAsync(AuthorizeUrl(), TestContext.Current.CancellationToken);
        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1>Sign in to the fake GitHub</h1>", page, StringComparison.Ordinal);
        Assert.Contains("person=github\"", page, StringComparison.Ordinal);   // the link ends with the person: the closing quote tells github from github-unverified
        Assert.Contains("person=github-unverified\"", page, StringComparison.Ordinal);
        Assert.Contains("person=github-no-primary\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"person\" value=\"custom\"", page, StringComparison.Ordinal);
        Assert.Contains("The provider verified this address", page, StringComparison.Ordinal);
        Assert.Contains("person=cancel", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_endpoint_redeems_a_code_once_with_its_verifier_in_json_or_as_a_form()   // criterion 32: as GitHub answers
    {
        var json = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub));
        var form = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub));

        using var asJson = await RedeemAsync(json["code"]);
        using var again = await RedeemAsync(json["code"]);
        using var asForm = await RedeemAsync(form["code"], json: false);

        var body = await JsonOfAsync(asJson);
        Assert.StartsWith("gho_", body["access_token"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("bearer", body["token_type"]!.GetValue<string>());
        Assert.Equal("user:email", body["scope"]!.GetValue<string>());
        Assert.Equal("bad_verification_code", (await JsonOfAsync(again))["error"]!.GetValue<string>());   // used once
        Assert.StartsWith("access_token=gho_", await asForm.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(2, GitHub.IssuedTokens.Count);
    }

    [Fact]
    public async Task A_wrong_verifier_or_a_refused_code_is_a_200_with_bad_verification_code_and_no_token()   // criterion 32: GitHub answers 200
    {
        var wrong = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub));
        var refused = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.RefuseCode));

        using var byVerifier = await RedeemAsync(wrong["code"], "another-verifier-of-at-least-forty-three-characters-000");
        using var byDefect = await RedeemAsync(refused["code"]);

        Assert.All([await JsonOfAsync(byVerifier), await JsonOfAsync(byDefect)], body =>
        {
            Assert.Equal("bad_verification_code", body["error"]!.GetValue<string>());
            Assert.Null(body["access_token"]);
        });
        Assert.Empty(GitHub.IssuedTokens);
    }

    [Fact]
    public async Task The_no_token_defect_answers_200_without_an_access_token()   // Amendment A3's input
    {
        var answer = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.NoToken));

        using var redeemed = await RedeemAsync(answer["code"]);

        var body = await JsonOfAsync(redeemed);
        Assert.Null(body["access_token"]);
        Assert.Equal("bearer", body["token_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task User_and_emails_answer_the_documented_json_for_the_bearer_token()   // criterion 32, U13
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));

        using var user = await ApiAsync("/user", token);
        using var emails = await ApiAsync("/user/emails?per_page=100", token);

        var me = await JsonOfAsync(user);
        Assert.Equal(JsonValueKind.Number, me["id"]!.GetValueKind());
        Assert.Equal(long.Parse(FakePeople.GitHubId, System.Globalization.CultureInfo.InvariantCulture), me["id"]!.GetValue<long>());
        Assert.Equal("ola-gh", me["login"]!.GetValue<string>());
        Assert.Equal(FakePeople.GitHubPublicEmail, me["email"]!.GetValue<string>());
        var entries = (await JsonOfAsync(emails)).AsArray();
        Assert.Equal(
            [(FakePeople.GitHubEmail, true, true), (FakePeople.GitHubSecondaryEmail, false, true)],
            entries.Select(e => (e!["email"]!.GetValue<string>(), e["primary"]!.GetValue<bool>(), e["verified"]!.GetValue<bool>())));
        Assert.Equal(["GET /user", "GET /user/emails?per_page=100"], GitHub.Requests.Where(r => r.StartsWith("GET", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Without_a_user_agent_and_githubs_accept_it_is_403_and_without_a_known_token_401()   // as GitHub answers
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));

        using var bare = await ApiAsync("/user", token, asGitHubAsks: false);
        using var unknown = await ApiAsync("/user", "gho_unknown");
        using var none = await ApiAsync("/user", null);

        Assert.Equal(HttpStatusCode.Forbidden, bare.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
    }

    [Theory]
    [InlineData(FakePeople.GitHubUnverified, FakePeople.GitHubUnverifiedId, "kim@github.test", true, false)]
    [InlineData(FakePeople.GitHubNoPrimary, FakePeople.GitHubNoPrimaryId, "lee@github.test", false, true)]
    public async Task The_other_two_people_of_the_spec_are_there(string person, string id, string firstEmail, bool primary, bool verified)   // criterion 32
    {
        var token = await AccessTokenAsync(new FakeSignIn(person));

        using var user = await ApiAsync("/user", token);
        using var emails = await ApiAsync("/user/emails?per_page=100", token);

        Assert.Equal(id, (await JsonOfAsync(user))["id"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var first = (await JsonOfAsync(emails)).AsArray()[0]!;
        Assert.Equal((firstEmail, primary, verified), (first["email"]!.GetValue<string>(), first["primary"]!.GetValue<bool>(), first["verified"]!.GetValue<bool>()));
        if (person == FakePeople.GitHubNoPrimary)
        {
            Assert.All((await JsonOfAsync(await ApiAsync("/user/emails?per_page=100", token))).AsArray(), e => Assert.False(e!["primary"]!.GetValue<bool>()));
        }
    }

    [Fact]
    public async Task A_free_address_is_the_same_github_identity_on_every_round_trip_and_verified_only_when_ticked()   // criterion 32: free-address mode
    {
        var once = await AccessTokenAsync(new FakeSignIn(FakePeople.Custom, "anna@firma.test", Verified: true));
        var twice = await AccessTokenAsync(new FakeSignIn(FakePeople.Custom, "ANNA@firma.test"));

        var first = await JsonOfAsync(await ApiAsync("/user", once));
        var second = await JsonOfAsync(await ApiAsync("/user", twice));
        var ticked = (await JsonOfAsync(await ApiAsync("/user/emails?per_page=100", once))).AsArray().Single()!;
        var unticked = (await JsonOfAsync(await ApiAsync("/user/emails?per_page=100", twice))).AsArray().Single()!;

        Assert.Equal(first["id"]!.GetValue<long>(), second["id"]!.GetValue<long>());   // derived from the address, whatever its case
        Assert.Equal(FakePeople.GitHubIdOf("anna@firma.test"), first["id"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(first["id"]!.GetValue<long>() > 0);
        Assert.Equal(("anna@firma.test", true, true), (ticked["email"]!.GetValue<string>(), ticked["primary"]!.GetValue<bool>(), ticked["verified"]!.GetValue<bool>()));
        Assert.False(unticked["verified"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(FakeDefects.UserFails, "/user", 500)]
    [InlineData(FakeDefects.EmailsFails, "/user/emails?per_page=100", 500)]
    public async Task A_failing_read_answers_500_on_demand(string defect, string path, int status)   // criterion 32
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: defect));

        using var response = await ApiAsync(path, token);

        Assert.Equal(status, (int)response.StatusCode);
    }

    [Theory]
    [InlineData(FakeDefects.UserNotJson, "/user")]
    [InlineData(FakeDefects.EmailsNotJson, "/user/emails?per_page=100")]
    public async Task An_answer_that_is_not_json_is_html_on_demand(string defect, string path)
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: defect));

        using var response = await ApiAsync(path, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_shape_defects_change_what_user_and_emails_say()   // the inputs of criterion 5
    {
        var noId = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.UserNoId));
        var badId = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.UserBadId));
        var large = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.UserTooLarge));
        var badEntry = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.EmailsBadEntry));
        var largeEmails = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.EmailsTooLarge));

        Assert.Null((await JsonOfAsync(await ApiAsync("/user", noId)))["id"]);
        Assert.Equal(JsonValueKind.String, (await JsonOfAsync(await ApiAsync("/user", badId)))["id"]!.GetValueKind());
        using var largeAnswer = await ApiAsync("/user", large);
        Assert.True((await largeAnswer.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length > 64 * 1024);
        Assert.Equal(JsonValueKind.String, (await JsonOfAsync(await ApiAsync("/user/emails?per_page=100", badEntry))).AsArray()[0]!["primary"]!.GetValueKind());
        using var largeEmailsAnswer = await ApiAsync("/user/emails?per_page=100", largeEmails);
        var largeBytes = await largeEmailsAnswer.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.True(largeBytes.Length > 64 * 1024);
        Assert.True(JsonNode.Parse(largeBytes)!.AsArray()[0]!["primary"]!.GetValue<bool>());   // documented JSON, only too large
    }

    [Theory]
    [InlineData(FakeDefects.UserSlow, "/user")]
    [InlineData(FakeDefects.EmailsSlow, "/user/emails?per_page=100")]
    public async Task The_slow_defects_hold_the_answer_until_the_request_is_aborted(string defect, string path)   // the input of the 10-second bound
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: defect));
        using var abort = new CancellationTokenSource();

        var pending = ApiAsync(path, token, cancel: abort.Token);
        await GitHub.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task A_slow_revocation_is_held_until_the_request_is_aborted_recorded_without_an_answer_and_the_token_stays()   // the input of the revocation's own bound
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.RevokeSlow));
        using var abort = new CancellationTokenSource();

        var pending = RevokeAsync(token, cancel: abort.Token);
        await GitHub.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(new GitHubRevocation(ClientId, token, 0), Assert.Single(GitHub.Revocations));
        using var after = await ApiAsync("/user", token);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task A_revocation_with_the_clients_basic_credentials_answers_204_records_the_token_and_ends_it()   // criterion 32
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));

        using var revoked = await RevokeAsync(token);
        using var after = await ApiAsync("/user", token);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(new GitHubRevocation(ClientId, token, 204), Assert.Single(GitHub.Revocations));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal(
            ["POST /login/oauth/access_token", "DELETE /applications/" + ClientId + "/token", "GET /user"], GitHub.Requests);
    }

    [Fact]
    public async Task A_failed_revocation_is_recorded_with_its_status_and_the_token_stays()   // criterion 32: a failing revocation on demand
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.RevokeFails));

        using var revoked = await RevokeAsync(token);
        using var after = await ApiAsync("/user", token);

        Assert.Equal(HttpStatusCode.InternalServerError, revoked.StatusCode);
        Assert.Equal(500, Assert.Single(GitHub.Revocations).Status);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task A_revocation_without_the_clients_credentials_is_401_and_recorded()
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));

        using var anonymous = await RevokeAsync(token, basicId: null);
        using var another = await RevokeAsync(token, basicId: "another-client");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, another.StatusCode);
        Assert.Equal([401, 401], GitHub.Revocations.Select(r => r.Status));
    }

    [Fact]
    public async Task The_test_issuer_serves_a_minimal_discovery_document_and_a_key_set_under_github()   // Amendment A5
    {
        await using var stack = new FakeOidcHost(_clock, new FakeOidcOptions
        {
            IssuerOrigin = new Uri("http://fake-oidc:8099"),
            BrowserOrigin = new Uri("http://localhost:8099"),
        });
        using var discovery = await stack.Client.GetAsync("http://fake-oidc:8099/github/.well-known/openid-configuration", TestContext.Current.CancellationToken);
        var document = JsonNode.Parse(await discovery.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        using var keys = await stack.Client.GetAsync("http://fake-oidc:8099/github/keys", TestContext.Current.CancellationToken);

        Assert.Equal("http://fake-oidc:8099/github", document["issuer"]!.GetValue<string>());
        Assert.Equal("http://localhost:8099/github/authorize", document["authorization_endpoint"]!.GetValue<string>());
        Assert.Equal("http://fake-oidc:8099/github/token", document["token_endpoint"]!.GetValue<string>());
        Assert.Equal("http://fake-oidc:8099/github/keys", document["jwks_uri"]!.GetValue<string>());
        Assert.Equal(["code"], document["response_types_supported"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(["S256"], document["code_challenge_methods_supported"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Null(document["authorization_response_iss_parameter_supported"]);
        Assert.Null(document["userinfo_endpoint"]);
        Assert.Single(JsonNode.Parse(await keys.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["keys"]!.AsArray());
    }

    [Fact]
    public async Task The_test_issuer_serves_the_token_the_api_and_the_revocation_under_github()   // Amendment A5, criterion 32
    {
        var answer = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub), "http://issuer.test/github/authorize");
        using var redeemed = await RedeemAsync(answer["code"], endpoint: "http://issuer.test/github/token");
        var token = (await JsonOfAsync(redeemed))["access_token"]!.GetValue<string>();

        using var user = await ApiAsync("/user", token, origin: "http://issuer.test/github");
        using var emails = await ApiAsync("/user/emails?per_page=100", token, origin: "http://issuer.test/github");
        using var revoked = await RevokeAsync(token, origin: "http://issuer.test/github");

        Assert.Equal(HttpStatusCode.OK, user.StatusCode);
        Assert.Equal(HttpStatusCode.OK, emails.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
    }

    [Fact]
    public async Task The_inspection_endpoint_lists_the_revocations_without_the_tokens()   // scripts/e2e-settings.sh reads it
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));
        using (var revoked = await RevokeAsync(token))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }

        using var listed = await _fake.Client.GetAsync("http://localhost:8099" + FakeGitHub.InspectionPath, TestContext.Current.CancellationToken);
        var text = await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal($$"""{"revocations":[{"client_id":"{{ClientId}}","status":204}]}""", text);
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("user_fail")]
    [InlineData(FakeDefects.NoSubject)]    // a defect of the OpenID fakes cannot apply to GitHub: refused, not ignored
    [InlineData(FakeDefects.WrongNonce)]
    [InlineData(FakeDefects.RefuseClient)]
    public async Task A_defect_the_fake_does_not_know_or_that_cannot_apply_is_refused_not_ignored(string defect)
    {
        using var response = await _fake.Client.GetAsync(
            new FakeSignIn(FakePeople.GitHub, Defect: defect).AppendTo(AuthorizeUrl()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task No_iss_cannot_apply_under_the_test_issuer_which_never_sends_iss_and_is_refused_there()   // a script that sets it on the stack must not get a good answer
    {
        using var response = await _fake.Client.GetAsync(
            new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.NoIss).AppendTo(AuthorizeUrl("http://issuer.test/github/authorize")),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_revocation_without_a_user_agent_or_without_githubs_accept_is_403_recorded_and_the_token_stays(bool userAgent, bool accept)
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));

        using var revoked = await RevokeAsync(token, userAgent: userAgent, accept: accept);
        using var after = await ApiAsync("/user", token);

        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal(new GitHubRevocation(ClientId, token, 403), Assert.Single(GitHub.Revocations));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Theory]
    [InlineData("another-client", Callback)]
    [InlineData(ClientId, "https://app.example.com/another/callback")]
    public async Task A_code_is_refused_for_another_client_or_another_redirect_address(string clientId, string redirect)   // criterion 32: as GitHub answers
    {
        var answer = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub));

        using var refused = await RedeemAsync(answer["code"], clientId: clientId, redirect: redirect);

        Assert.Equal("bad_verification_code", (await JsonOfAsync(refused))["error"]!.GetValue<string>());
        Assert.Empty(GitHub.IssuedTokens);
    }

    [Fact]
    public async Task The_client_id_may_come_in_the_basic_credentials_and_the_requests_are_listed_in_order()   // client_secret_basic
    {
        var answer = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub));
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = answer["code"],
                ["redirect_uri"] = Callback,
                ["code_verifier"] = Verifier,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":any-secret")));

        using var redeemed = await _fake.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.StartsWith("gho_", (await JsonOfAsync(redeemed))["access_token"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(["POST /login/oauth/access_token"], GitHub.Requests);
    }

    [Fact]
    public async Task A_subject_and_an_address_a_script_asks_for_replace_the_defaults_of_the_primary_entry()   // the e2e script uses a fixed id
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, "ola.new@github.test", Verified: false, Subject: "70000099"));

        var me = await JsonOfAsync(await ApiAsync("/user", token));
        var first = (await JsonOfAsync(await ApiAsync("/user/emails?per_page=100", token))).AsArray()[0]!;

        Assert.Equal(70000099L, me["id"]!.GetValue<long>());
        Assert.Equal(("ola.new@github.test", true, false), (first["email"]!.GetValue<string>(), first["primary"]!.GetValue<bool>(), first["verified"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task A_free_address_person_without_an_address_is_refused()   // GitHub has no such person
    {
        using var response = await _fake.Client.GetAsync(new FakeSignIn(FakePeople.Custom).AppendTo(AuthorizeUrl()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task A_revocation_without_a_json_body_is_422_and_recorded_without_a_token()
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{Api}/applications/{ClientId}/token");
        request.Headers.UserAgent.ParseAdd("Auth-Core");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":any-secret")));

        using var revoked = await _fake.Client.SendAsync(request, TestContext.Current.CancellationToken);
        using var after = await ApiAsync("/user", token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, revoked.StatusCode);
        Assert.Equal(new GitHubRevocation(ClientId, "", 422), Assert.Single(GitHub.Revocations));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task A_discovery_document_without_configured_origins_names_the_origin_of_the_request()   // Amendment A5, in process
    {
        using var discovery = await _fake.Client.GetAsync("http://issuer.test/github/.well-known/openid-configuration", TestContext.Current.CancellationToken);
        var document = await JsonOfAsync(discovery);

        Assert.Equal("http://issuer.test/github", document["issuer"]!.GetValue<string>());
        Assert.Equal("http://issuer.test/github/authorize", document["authorization_endpoint"]!.GetValue<string>());
        Assert.Equal("http://issuer.test/github/token", document["token_endpoint"]!.GetValue<string>());
        Assert.Equal("http://issuer.test/github/keys", document["jwks_uri"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("http://issuer.test/github/nope")]
    [InlineData("http://issuer.test/github")]
    [InlineData("https://api.github.com/orgs")]
    [InlineData("https://github.com/login/oauth/nope")]
    public async Task A_path_github_does_not_serve_is_404(string url)
    {
        using var response = await _fake.Client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_held_request_is_released_when_the_fake_is_disposed()   // a slow defect never outlives its test
    {
        await using var stack = new FakeOidcHost(_clock);
        using var authorized = await stack.Client.GetAsync(
            new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.UserSlow).AppendTo(AuthorizeUrl()), TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = QueryHelpers.ParseQuery(authorized.Headers.Location!.Query)["code"].ToString(),
            ["redirect_uri"] = Callback,
            ["client_id"] = ClientId,
            ["code_verifier"] = Verifier,
        });
        using var redeemed = await stack.Client.PostAsync(TokenEndpoint, form, TestContext.Current.CancellationToken);
        var token = QueryHelpers.ParseQuery(await redeemed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))["access_token"].ToString();   // no Accept: GitHub answers form-encoded
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Auth-Core");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        var pending = stack.Client.SendAsync(request, TestContext.Current.CancellationToken);
        await stack.Server.GitHub.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        stack.Server.Dispose();   // releases the held request through FakeOidcServer.Dispose; the host disposes the server again afterwards

        using var answer = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Either_a_missing_user_agent_or_a_missing_github_accept_alone_is_403(bool userAgent, bool accept)   // as GitHub answers
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (userAgent)
        {
            request.Headers.UserAgent.ParseAdd("Auth-Core");
        }

        if (accept)
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
        }

        using var response = await _fake.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
