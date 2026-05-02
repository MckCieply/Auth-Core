using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Time.Testing;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, criterion 34: the fake provider serves discovery, authorization, token and key-set endpoints and the three people.</summary>
public sealed class FakeOidcTests : IAsyncDisposable
{
    private const string Verifier = "a-verifier-of-at-least-forty-three-characters-0123456789";
    private const string ClientId = "the-client";
    private const string GoogleCallback = "https://app.example.com/auth/providers/google/callback";
    private const string MicrosoftCallback = "https://app.example.com/auth/providers/microsoft/callback";
    private const string GoogleAuthorize = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string MicrosoftAuthorize = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeOidcHost _fake;

    public FakeOidcTests()
    {
        _fake = new FakeOidcHost(_clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _fake.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static string Challenge(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string AuthorizeUrl(string endpoint, string redirect, string clientId = ClientId) =>
        QueryHelpers.AddQueryString(endpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["scope"] = "openid email profile",
            ["state"] = "the-state",
            ["nonce"] = "the-nonce",
            ["code_challenge"] = Challenge(Verifier),
            ["code_challenge_method"] = "S256",
        });

    private async Task<JsonObject> JsonAsync(string url)
    {
        using var response = await _fake.Client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
    }

    /// <summary>The query of the fake's redirect back to the callback.</summary>
    private async Task<Dictionary<string, string>> AuthorizeAsync(string endpoint, string redirect, FakeSignIn person)
    {
        using var response = await _fake.Client.GetAsync(person.AppendTo(AuthorizeUrl(endpoint, redirect)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(redirect + "?", location.AbsoluteUri, StringComparison.Ordinal);
        return QueryHelpers.ParseQuery(location.Query).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
    }

    private async Task<HttpResponseMessage> RedeemAsync(string tokenEndpoint, string code, string redirect, string verifier = Verifier, string clientId = ClientId)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["client_id"] = clientId,
            ["client_secret"] = "any-secret",
            ["code_verifier"] = verifier,
        });
        return await _fake.Client.PostAsync(tokenEndpoint, form, TestContext.Current.CancellationToken);
    }

    private static JsonObject PayloadOf(string token) => JsonNode.Parse(Base64UrlEncoder.Decode(token.Split('.')[1]))!.AsObject();

    private static readonly Regex Links = new("href=\"([^\"]*)\"", RegexOptions.None, TimeSpan.FromSeconds(5));
    private static readonly Regex HiddenInputs = new("<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">", RegexOptions.None, TimeSpan.FromSeconds(5));
    private static readonly Regex FormAction = new("<form method=\"get\" action=\"([^\"]*)\">", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>The identity token of a full round trip for this person, its payload as JSON.</summary>
    private async Task<(string Token, JsonObject Payload)> IdentityTokenAsync(bool google, FakeSignIn person)
    {
        var answer = await AuthorizeAsync(google ? GoogleAuthorize : MicrosoftAuthorize, google ? GoogleCallback : MicrosoftCallback, person);
        using var redeemed = await RedeemAsync(
            google ? "https://oauth2.googleapis.com/token" : "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            answer["code"],
            google ? GoogleCallback : MicrosoftCallback);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var token = JsonNode.Parse(await redeemed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["id_token"]!.GetValue<string>();
        return (token, JsonNode.Parse(Base64UrlEncoder.Decode(token.Split('.')[1]))!.AsObject());
    }

    [Fact]
    public async Task Google_discovery_names_googles_endpoints_and_the_iss_parameter()   // criterion 34, Amendment A5
    {
        var document = await JsonAsync("https://accounts.google.com/.well-known/openid-configuration");

        Assert.Equal(FakeOidcServer.GoogleIssuer, document["issuer"]!.GetValue<string>());
        Assert.Equal(GoogleAuthorize, document["authorization_endpoint"]!.GetValue<string>());
        Assert.Equal("https://oauth2.googleapis.com/token", document["token_endpoint"]!.GetValue<string>());
        Assert.Equal("https://www.googleapis.com/oauth2/v3/certs", document["jwks_uri"]!.GetValue<string>());
        Assert.True(document["authorization_response_iss_parameter_supported"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Microsoft_discovery_names_the_common_endpoints_and_the_tenant_placeholder()   // criterion 34
    {
        var document = await JsonAsync("https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration");

        Assert.Equal(FakeOidcServer.MicrosoftIssuerTemplate, document["issuer"]!.GetValue<string>());
        Assert.Equal(MicrosoftAuthorize, document["authorization_endpoint"]!.GetValue<string>());
        Assert.Equal("https://login.microsoftonline.com/common/discovery/v2.0/keys", document["jwks_uri"]!.GetValue<string>());
        Assert.Null(document["code_challenge_methods_supported"]);
        Assert.Null(document["authorization_response_iss_parameter_supported"]);
    }

    [Fact]
    public async Task The_test_issuer_names_the_browser_origin_for_the_authorization_and_its_own_for_the_rest()   // criterion 34
    {
        await using var container = new FakeOidcHost(
            _clock, new FakeOidcOptions { IssuerOrigin = new Uri("http://fake-oidc:8099"), BrowserOrigin = new Uri("http://localhost:8099") });
        using var response = await container.Client.GetAsync("http://fake-oidc:8099/microsoft/.well-known/openid-configuration", TestContext.Current.CancellationToken);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();

        Assert.Equal("http://fake-oidc:8099/microsoft", document["issuer"]!.GetValue<string>());
        Assert.Equal("http://localhost:8099/microsoft/authorize", document["authorization_endpoint"]!.GetValue<string>());
        Assert.Equal("http://fake-oidc:8099/microsoft/token", document["token_endpoint"]!.GetValue<string>());
        Assert.Equal("http://fake-oidc:8099/microsoft/keys", document["jwks_uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_person_answers_at_once_with_a_code_the_state_and_for_google_iss()   // criterion 34: the non-interactive mode
    {
        var google = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google));
        var microsoft = await AuthorizeAsync(MicrosoftAuthorize, MicrosoftCallback, new FakeSignIn(FakePeople.MicrosoftWork));

        Assert.Equal(["code", "iss", "state"], google.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("the-state", google["state"]);
        Assert.Equal(FakeOidcServer.GoogleIssuer, google["iss"]);
        Assert.Equal(["code", "state"], microsoft.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Without_a_person_the_page_offers_the_people_a_free_address_and_cancel()   // Settled 22
    {
        using var google = await _fake.Client.GetAsync(AuthorizeUrl(GoogleAuthorize, GoogleCallback), TestContext.Current.CancellationToken);
        using var microsoft = await _fake.Client.GetAsync(AuthorizeUrl(MicrosoftAuthorize, MicrosoftCallback), TestContext.Current.CancellationToken);
        var googlePage = await google.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var microsoftPage = await microsoft.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, google.StatusCode);
        Assert.Equal("text/html", google.Content.Headers.ContentType?.MediaType);
        Assert.Contains(FakePeople.GoogleEmail, googlePage, StringComparison.Ordinal);
        Assert.Contains("name=\"email\"", googlePage, StringComparison.Ordinal);
        Assert.Contains("name=\"verified\"", googlePage, StringComparison.Ordinal);
        Assert.Contains(">Cancel<", googlePage, StringComparison.Ordinal);
        Assert.Contains(FakePeople.PersonalEmail, microsoftPage, StringComparison.Ordinal);
        Assert.Contains(FakePeople.WorkEmail, microsoftPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_answers_access_denied_with_the_state()   // criterion 8: what a person who cancels causes
    {
        var answer = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Cancel));

        Assert.Equal("access_denied", answer["error"]);
        Assert.Equal("the-state", answer["state"]);
        Assert.Equal(FakeOidcServer.RefusalDescription, answer["error_description"]);
        Assert.Equal(FakeOidcServer.GoogleIssuer, answer["iss"]);   // Amendment A5: every Google answer carries iss, the error answers too
        Assert.False(answer.ContainsKey("code"));
    }

    [Fact]
    public async Task The_token_endpoint_redeems_a_code_once_with_its_verifier_and_signs_with_the_published_key()   // criterion 34
    {
        var answer = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google));
        using var first = await RedeemAsync("https://oauth2.googleapis.com/token", answer["code"], GoogleCallback);
        using var second = await RedeemAsync("https://oauth2.googleapis.com/token", answer["code"], GoogleCallback);
        var keys = new JsonWebKeySet((await JsonAsync("https://www.googleapis.com/oauth2/v3/certs")).ToJsonString());
        var token = JsonNode.Parse(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["id_token"]!.GetValue<string>();

        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = FakeOidcServer.GoogleIssuer,
            ValidAudience = ClientId,
            IssuerSigningKeys = keys.GetSigningKeys(),
            LifetimeValidator = (_, _, _, _) => true,
        });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(validated.IsValid, validated.Exception?.Message);
        Assert.Equal(FakePeople.GoogleSubject, validated.Claims["sub"]);
        Assert.Equal(FakePeople.GoogleEmail, validated.Claims["email"]);
        Assert.Equal(true, validated.Claims["email_verified"]);
        Assert.Equal("the-nonce", validated.Claims["nonce"]);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Single(_fake.Server.IssuedIdentityTokens);
    }

    [Fact]
    public async Task A_wrong_verifier_another_client_or_a_refused_code_is_invalid_grant_with_a_description()   // criterion 8: the code the provider refuses
    {
        var one = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google));
        var two = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google));
        var three = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google, Defect: FakeDefects.RefuseCode));

        using var wrongVerifier = await RedeemAsync("https://oauth2.googleapis.com/token", one["code"], GoogleCallback, verifier: Verifier + "x");
        using var otherClient = await RedeemAsync("https://oauth2.googleapis.com/token", two["code"], GoogleCallback, clientId: "another-client");
        using var refused = await RedeemAsync("https://oauth2.googleapis.com/token", three["code"], GoogleCallback);

        foreach (var response in new[] { wrongVerifier, otherClient, refused })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal("invalid_grant", body["error"]!.GetValue<string>());
            Assert.Equal(FakeOidcServer.RefusalDescription, body["error_description"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_refused_client_is_invalid_client_with_a_description()   // spec 0011 → the runbook: a wrong or lapsed client secret
    {
        var answer = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google, Defect: FakeDefects.RefuseClient));

        using var refused = await RedeemAsync("https://oauth2.googleapis.com/token", answer["code"], GoogleCallback);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = JsonNode.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("invalid_client", body["error"]!.GetValue<string>());
        Assert.Equal(FakeOidcServer.RefusalDescription, body["error_description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(FakeDefects.NoSubject, "sub", null)]
    [InlineData(FakeDefects.NoEmail, "email", null)]
    [InlineData(FakeDefects.WrongAudience, "aud", "another-client")]
    [InlineData(FakeDefects.WrongNonce, "nonce", "another-nonce")]
    [InlineData(FakeDefects.WrongIssuer, "iss", "https://evil.example")]
    public async Task A_defect_changes_what_a_google_identity_token_says(string defect, string claim, string? value)   // criterion 34: on demand
    {
        var (_, payload) = await IdentityTokenAsync(google: true, new FakeSignIn(FakePeople.Google, Defect: defect));

        Assert.Equal(value, payload[claim]?.GetValue<string>());
    }

    [Theory]
    [InlineData(FakeDefects.NoTenant, "tid")]
    [InlineData(FakeDefects.NoObjectId, "oid")]
    public async Task A_defect_leaves_a_claim_out_of_a_microsoft_identity_token(string defect, string claim)   // criterion 34
    {
        var (_, payload) = await IdentityTokenAsync(google: false, new FakeSignIn(FakePeople.MicrosoftWork, Defect: defect));

        Assert.Null(payload[claim]);
    }

    [Fact]
    public async Task A_bad_signature_is_made_with_a_key_that_is_not_published()   // criterion 34
    {
        var (token, _) = await IdentityTokenAsync(google: true, new FakeSignIn(FakePeople.Google, Defect: FakeDefects.BadSignature));
        var keys = new JsonWebKeySet((await JsonAsync("https://www.googleapis.com/oauth2/v3/certs")).ToJsonString());

        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = FakeOidcServer.GoogleIssuer,
            ValidAudience = ClientId,
            IssuerSigningKeys = keys.GetSigningKeys(),
            LifetimeValidator = (_, _, _, _) => true,
        });

        Assert.False(validated.IsValid);
        Assert.IsType<SecurityTokenInvalidSignatureException>(validated.Exception);   // the same parameters accept an honest token above
    }

    [Fact]
    public async Task The_three_people_of_the_spec_are_there()   // criterion 34: Google, Microsoft personal, Microsoft work with an unverified address
    {
        var (_, google) = await IdentityTokenAsync(google: true, new FakeSignIn(FakePeople.Google));
        var (_, personal) = await IdentityTokenAsync(google: false, new FakeSignIn(FakePeople.MicrosoftPersonal));
        var (_, work) = await IdentityTokenAsync(google: false, new FakeSignIn(FakePeople.MicrosoftWork));

        Assert.True(google["email_verified"]!.GetValue<bool>());
        Assert.Equal(FakePeople.PersonalTenant, personal["tid"]!.GetValue<string>());
        Assert.Equal(FakePeople.PersonalObjectId, personal["oid"]!.GetValue<string>());
        Assert.Equal(FakePeople.PersonalEmail, personal["email"]!.GetValue<string>());
        Assert.Equal($"https://login.microsoftonline.com/{FakePeople.PersonalTenant}/v2.0", personal["iss"]!.GetValue<string>());
        Assert.Equal(FakePeople.WorkTenant, work["tid"]!.GetValue<string>());
        Assert.Equal(FakePeople.WorkEmail, work["email"]!.GetValue<string>());
        Assert.Null(work["xms_edov"]);
        Assert.NotEqual(FakePeople.WorkEmail, work["preferred_username"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("true", JsonValueKind.True)]
    [InlineData("false", JsonValueKind.False)]
    [InlineData("1", JsonValueKind.String)]
    public async Task The_work_person_carries_xms_edov_as_asked(string edov, JsonValueKind kind)   // criterion 9's inputs
    {
        var (_, work) = await IdentityTokenAsync(google: false, new FakeSignIn(FakePeople.MicrosoftWork, Edov: edov));

        Assert.Equal(kind, work["xms_edov"]!.GetValueKind());
    }

    [Fact]
    public async Task A_free_address_is_the_same_identity_on_every_round_trip_and_verified_only_when_ticked()   // Settled 22
    {
        var (_, first) = await IdentityTokenAsync(google: true, new FakeSignIn(FakePeople.Custom, Email: "new@e2e.test"));
        var (_, again) = await IdentityTokenAsync(google: true, new FakeSignIn(FakePeople.Custom, Email: "NEW@e2e.test", Verified: true));
        var (_, microsoft) = await IdentityTokenAsync(google: false, new FakeSignIn(FakePeople.Custom, Email: "new@e2e.test", Verified: true));

        Assert.Equal(first["sub"]!.GetValue<string>(), again["sub"]!.GetValue<string>());
        Assert.False(first["email_verified"]!.GetValue<bool>());
        Assert.True(again["email_verified"]!.GetValue<bool>());
        Assert.True(microsoft["xms_edov"]!.GetValue<bool>());
        Assert.True(Guid.TryParse(microsoft["oid"]!.GetValue<string>(), out _));
    }

    private static string TokenEndpointOf(bool google) =>
        google ? "https://oauth2.googleapis.com/token" : "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    private async Task<JsonObject> PayloadOfCodeAsync(bool google, string code)
    {
        using var redeemed = await RedeemAsync(TokenEndpointOf(google), code, google ? GoogleCallback : MicrosoftCallback);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        return PayloadOf(JsonNode.Parse(await redeemed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["id_token"]!.GetValue<string>());
    }

    /// <summary>Follows an absolute address once, without following the redirect it answers; the query of a 302 back to the callback.</summary>
    private async Task<Dictionary<string, string>> FollowAsync(Uri uri, string callback)
    {
        using var response = await _fake.Client.GetAsync(uri, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith(callback + "?", response.Headers.Location!.AbsoluteUri, StringComparison.Ordinal);
        return QueryHelpers.ParseQuery(response.Headers.Location.Query).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_pages_links_and_form_lead_to_the_redirects_they_name(bool google)   // Settled 22: what the browser does on the page
    {
        var callback = google ? GoogleCallback : MicrosoftCallback;
        var pageUrl = AuthorizeUrl(google ? GoogleAuthorize : MicrosoftAuthorize, callback) + "&defect=" + FakeDefects.NoEmail;
        using var page = await _fake.Client.GetAsync(pageUrl, TestContext.Current.CancellationToken);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var origin = new Uri(pageUrl);
        var links = Links.Matches(html).Select(m => new Uri(origin, WebUtility.HtmlDecode(m.Groups[1].Value))).ToList();
        string[] subjects = google ? [FakePeople.GoogleSubject] : [FakePeople.PersonalObjectId, FakePeople.WorkObjectId];

        Assert.Equal(subjects.Length + 1, links.Count);
        for (var i = 0; i < subjects.Length; i++)
        {
            var answer = await FollowAsync(links[i], callback);
            var payload = await PayloadOfCodeAsync(google, answer["code"]);

            Assert.Equal("the-state", answer["state"]);
            Assert.Equal(subjects[i], payload[google ? "sub" : "oid"]!.GetValue<string>());
            Assert.Null(payload["email"]);   // the defect of the page's own address went with the link
        }

        var cancelled = await FollowAsync(links[^1], callback);
        Assert.Equal("access_denied", cancelled["error"]);
        Assert.Equal("the-state", cancelled["state"]);

        var action = new Uri(origin, WebUtility.HtmlDecode(FormAction.Match(html).Groups[1].Value));
        var fields = HiddenInputs.Matches(html)
            .Select(m => new KeyValuePair<string, string?>(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
            .Append(new("email", "new@e2e.test"))
            .Append(new("verified", "true"));
        var submitted = await FollowAsync(new Uri(QueryHelpers.AddQueryString(action.GetLeftPart(UriPartial.Path), fields)), callback);
        var custom = await PayloadOfCodeAsync(google, submitted["code"]);

        Assert.Equal("the-state", submitted["state"]);
        Assert.Null(custom["email"]);
        Assert.True(custom[google ? "email_verified" : "xms_edov"]!.GetValue<bool>());
        Assert.NotEqual(subjects[0], custom[google ? "sub" : "oid"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true, FakePeople.Google, "typo")]
    [InlineData(true, FakePeople.Google, FakeDefects.NoTenant)]
    [InlineData(true, FakePeople.Google, FakeDefects.NoObjectId)]
    [InlineData(false, FakePeople.MicrosoftWork, "typo")]
    [InlineData(true, FakePeople.Cancel, "typo")]
    public async Task A_defect_the_fake_does_not_know_or_that_cannot_apply_is_refused_not_ignored(bool google, string person, string defect)   // a typo never gives a good token
    {
        var url = new FakeSignIn(person, Defect: defect).AppendTo(AuthorizeUrl(google ? GoogleAuthorize : MicrosoftAuthorize, google ? GoogleCallback : MicrosoftCallback));

        using var response = await _fake.Client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_subject_a_tenant_and_an_address_a_script_asks_for_replace_the_defaults()   // Tasks 8 to 12 and the e2e script use a fixed subject
    {
        const string ObjectId = "00000000-0000-0000-0000-0000000000ff";
        const string Tenant = "11111111-0000-4000-8000-000000000001";

        var (_, google) = await IdentityTokenAsync(google: true, new FakeSignIn(FakePeople.Google, "x@y.test", Verified: false, Subject: "fixed-sub"));
        var (_, work) = await IdentityTokenAsync(
            google: false, new FakeSignIn(FakePeople.MicrosoftWork, "w@y.test", Subject: ObjectId, Tenant: Tenant, Edov: "1"));

        Assert.Equal("fixed-sub", google["sub"]!.GetValue<string>());
        Assert.Equal("x@y.test", google["email"]!.GetValue<string>());
        Assert.False(google["email_verified"]!.GetValue<bool>());
        Assert.Equal(ObjectId, work["oid"]!.GetValue<string>());
        Assert.Equal(Tenant, work["tid"]!.GetValue<string>());
        Assert.Equal("w@y.test", work["email"]!.GetValue<string>());
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}/v2.0", work["iss"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.String, work["xms_edov"]!.GetValueKind());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_provider_error_defect_answers_server_error_with_the_state_and_for_google_iss(bool google)   // criterion 8: provider_error
    {
        var answer = await AuthorizeAsync(
            google ? GoogleAuthorize : MicrosoftAuthorize, google ? GoogleCallback : MicrosoftCallback,
            new FakeSignIn(google ? FakePeople.Google : FakePeople.MicrosoftWork, Defect: FakeDefects.ProviderError));

        Assert.Equal("server_error", answer["error"]);
        Assert.Equal("the-state", answer["state"]);
        Assert.Equal(FakeOidcServer.RefusalDescription, answer["error_description"]);
        Assert.False(answer.ContainsKey("code"));
        Assert.Equal(google, answer.ContainsKey("iss"));
    }

    [Fact]
    public async Task Microsoft_publishes_the_key_that_signs_its_tokens()   // criterion 34: Microsoft's key-set endpoint
    {
        var (token, _) = await IdentityTokenAsync(google: false, new FakeSignIn(FakePeople.MicrosoftPersonal));
        var keys = new JsonWebKeySet((await JsonAsync("https://login.microsoftonline.com/common/discovery/v2.0/keys")).ToJsonString());

        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = $"https://login.microsoftonline.com/{FakePeople.PersonalTenant}/v2.0",
            ValidAudience = ClientId,
            IssuerSigningKeys = keys.GetSigningKeys(),
            LifetimeValidator = (_, _, _, _) => true,
        });

        Assert.True(validated.IsValid, validated.Exception?.Message);
    }

    [Theory]
    [InlineData(FakeKinds.Google)]
    [InlineData(FakeKinds.Microsoft)]
    public async Task The_test_issuer_serves_authorization_token_and_keys_under_its_own_paths(string kind)   // criterion 34
    {
        var google = kind == FakeKinds.Google;
        var issuer = "http://issuer.test/" + kind;
        var callback = "http://app.test/auth/providers/" + kind + "/callback";
        var document = await JsonAsync(issuer + "/.well-known/openid-configuration");
        var person = new FakeSignIn(google ? FakePeople.Google : FakePeople.MicrosoftWork);

        var answer = await AuthorizeAsync(document["authorization_endpoint"]!.GetValue<string>(), callback, person);
        using var redeemed = await RedeemAsync(document["token_endpoint"]!.GetValue<string>(), answer["code"], callback);
        var token = JsonNode.Parse(await redeemed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["id_token"]!.GetValue<string>();
        var keys = new JsonWebKeySet((await JsonAsync(document["jwks_uri"]!.GetValue<string>())).ToJsonString());
        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = ClientId,
            IssuerSigningKeys = keys.GetSigningKeys(),
            LifetimeValidator = (_, _, _, _) => true,
        });

        Assert.Equal(google, answer.ContainsKey("iss"));
        Assert.Equal(google ? issuer : null, answer.GetValueOrDefault("iss"));
        Assert.True(validated.IsValid, validated.Exception?.Message);
    }

    [Fact]
    public async Task A_wrong_issuer_names_another_tenant_at_microsoft_and_another_issuer_at_the_test_issuer()   // Amendment A6
    {
        var (_, web) = await IdentityTokenAsync(google: false, new FakeSignIn(FakePeople.MicrosoftWork, Defect: FakeDefects.WrongIssuer));
        var answer = await AuthorizeAsync(
            "http://issuer.test/google/authorize", "http://app.test/cb", new FakeSignIn(FakePeople.Google, Defect: FakeDefects.WrongIssuer));
        using var redeemed = await RedeemAsync("http://issuer.test/google/token", answer["code"], "http://app.test/cb");
        var test = PayloadOf(JsonNode.Parse(await redeemed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["id_token"]!.GetValue<string>());

        Assert.Equal($"https://login.microsoftonline.com/{FakeOidcServer.OtherTenant}/v2.0", web["iss"]!.GetValue<string>());
        Assert.Equal(FakePeople.WorkTenant, web["tid"]!.GetValue<string>());
        Assert.Equal("http://issuer.test/google-other", test["iss"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("state")]
    [InlineData("nonce")]
    [InlineData("code_challenge")]
    [InlineData("client_id")]
    [InlineData("redirect_uri")]
    public async Task An_authorization_request_without_a_required_parameter_is_refused(string missing)   // the fake checks what a provider checks
    {
        var parameters = QueryHelpers.ParseQuery(new Uri(AuthorizeUrl(GoogleAuthorize, GoogleCallback)).Query)
            .Where(p => p.Key != missing)
            .Select(p => new KeyValuePair<string, string?>(p.Key, p.Value.ToString()));
        var url = new FakeSignIn(FakePeople.Google).AppendTo(QueryHelpers.AddQueryString(GoogleAuthorize, parameters));

        using var response = await _fake.Client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("code_challenge_method", "plain")]
    [InlineData("response_type", "token")]
    public async Task An_authorization_request_that_is_not_the_code_flow_with_s256_is_refused(string name, string value)
    {
        var parameters = QueryHelpers.ParseQuery(new Uri(AuthorizeUrl(GoogleAuthorize, GoogleCallback)).Query)
            .Select(p => new KeyValuePair<string, string?>(p.Key, p.Key == name ? value : p.Value.ToString()));
        var url = new FakeSignIn(FakePeople.Google).AppendTo(QueryHelpers.AddQueryString(GoogleAuthorize, parameters));

        using var response = await _fake.Client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("https://accounts.google.com/nope")]
    [InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/other")]
    [InlineData("http://issuer.test/github/authorize")]
    [InlineData("http://issuer.test/google")]
    public async Task An_unknown_path_is_404(string url)
    {
        using var response = await _fake.Client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_token_endpoint_reads_the_client_id_of_client_secret_basic()
    {
        var one = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google));
        var two = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google));

        async Task<HttpResponseMessage> RedeemWithBasicAsync(string code, string clientId)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = GoogleCallback,
                    ["code_verifier"] = Verifier,
                }),
            };
            request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":any-secret")));
            return await _fake.Client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        using var accepted = await RedeemWithBasicAsync(one["code"], ClientId);
        using var refused = await RedeemWithBasicAsync(two["code"], "another-client");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    private static FakeOidcOptions OptionsOf(params (string Key, string? Value)[] values) => FakeOidcOptions.From(
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

    [Fact]
    public void Without_origins_in_the_configuration_the_options_have_none()
    {
        var empty = OptionsOf();
        var blank = OptionsOf((FakeOidcOptions.IssuerOriginKey, "   "), (FakeOidcOptions.BrowserOriginKey, ""));

        Assert.Null(empty.IssuerOrigin);
        Assert.Null(empty.BrowserOrigin);
        Assert.Null(blank.IssuerOrigin);
        Assert.Null(blank.BrowserOrigin);
    }

    [Fact]
    public void An_origin_is_trimmed_and_loses_its_path_and_query()
    {
        var options = OptionsOf(
            (FakeOidcOptions.IssuerOriginKey, " http://fake-oidc:8099/some/path?x=1 "), (FakeOidcOptions.BrowserOriginKey, "http://localhost:8099"));

        Assert.Equal("http://fake-oidc:8099/", options.IssuerOrigin!.AbsoluteUri);
        Assert.Equal("http://localhost:8099/", options.BrowserOrigin!.AbsoluteUri);
    }

    [Theory]
    [InlineData(FakeOidcOptions.IssuerOriginKey)]
    [InlineData(FakeOidcOptions.BrowserOriginKey)]
    public void An_origin_that_is_not_a_url_stops_the_start_naming_its_key(string key)
    {
        var error = Assert.Throws<InvalidOperationException>(() => OptionsOf((key, "not a url")));

        Assert.Contains(key, error.Message, StringComparison.Ordinal);
    }
}
