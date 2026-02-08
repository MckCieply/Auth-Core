using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Microsoft.IdentityModel.Tokens;

namespace Auth.IntegrationTests;

public sealed class BearerValidationTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private static string Tamper(string jwt, Action<JsonObject> change)
    {
        var parts = jwt.Split('.');
        var payload = AccessTokens.Payload(jwt);
        change(payload);
        return string.Join('.', parts[0], Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payload.ToJsonString())), parts[2]);
    }

    private async Task<HttpResponseMessage> MeAsync(string? token) => await TenancyApi.Get(Client, OrgEndpoints.MePath, token);

    [Fact]
    public async Task A_valid_token_is_accepted()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        using var response = await MeAsync(session.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Request_without_a_token_is_a_401_with_an_empty_body_and_a_bearer_challenge()   // criterion 14
    {
        using var response = await MeAsync(null);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_that_is_not_a_bearer_token_is_a_401()
    {
        using var request = TenancyApi.Request(HttpMethod.Get, OrgEndpoints.MePath, null);
        request.Headers.TryAddWithoutValidation("Authorization", "Basic Zm9vOmJhcg==");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_with_a_changed_signature_is_a_401()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);
        var parts = session.AccessToken.Split('.');

        using var response = await MeAsync(string.Join('.', parts[0], parts[1], (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..]));

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_with_a_changed_payload_is_a_401()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        // The roles are rewritten, the signature stays: whoever does this must not get the company of someone else.
        using var response = await MeAsync(Tamper(session.AccessToken, payload => payload["org_id"] = Guid.NewGuid().ToString()));

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    [InlineData("")]
    public async Task Something_that_is_not_a_token_is_a_401(string token)
    {
        using var response = await MeAsync(token);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_without_a_signature_is_a_401()
    {
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"at+jwt"}""");
        var payload = Base64UrlEncoder.Encode(AccessTokens.Payload((await SessionApi.LoginAsync(Client, Factory)).AccessToken).ToJsonString());

        using var response = await MeAsync($"{header}.{payload}.");

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_for_another_audience_is_a_401()   // without AddAudiences it would pass
    {
        await using var other = new AuthAppFactory(Postgres, Keys).WithSetting("Auth:Tokens:Audience", "some-other-product");
        using var otherClient = SessionApi.CreateClient(other);
        var foreign = await LoginApi.LoginToken(otherClient, other);

        using var response = await MeAsync(foreign);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_from_another_issuer_is_a_401()
    {
        await using var other = new AuthAppFactory(Postgres, Keys).WithSetting("Auth:Tokens:Issuer", "http://evil.example/auth");
        using var otherClient = SessionApi.CreateClient(other);
        var foreign = await LoginApi.LoginToken(otherClient, other);

        using var response = await MeAsync(foreign);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task An_expired_token_is_a_401()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        Clock.Advance(TimeSpan.FromMinutes(9));
        using (var live = await MeAsync(session.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }

        Clock.Advance(TimeSpan.FromMinutes(2));
        using var response = await MeAsync(session.AccessToken);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task The_challenge_of_a_bad_token_names_the_bearer_scheme_and_the_error_of_rfc_6750()
    {
        using var response = await MeAsync("garbage");

        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("invalid_token", challenge.Parameter);
    }

    [Fact]
    public async Task The_challenge_without_a_token_is_plain_bearer_with_no_error()   // RFC 6750 §3.1
    {
        using var response = await MeAsync(null);

        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.DoesNotContain("error", challenge.Parameter ?? "");
    }

    [Fact]
    public async Task The_challenge_of_an_expired_token_names_invalid_token()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        Clock.Advance(TimeSpan.FromMinutes(11));
        using var response = await MeAsync(session.AccessToken);

        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("error=\"invalid_token\"", challenge.Parameter);
    }

    [Fact]
    public async Task A_valid_token_in_the_query_string_is_not_accepted()   // a token in a URL lands in logs
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        using var response = await TenancyApi.Get(Client, $"{OrgEndpoints.MePath}?access_token={Uri.EscapeDataString(session.AccessToken)}", null);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_valid_token_in_a_form_body_is_not_accepted()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);
        using var request = TenancyApi.Request(HttpMethod.Patch, OrgEndpoints.OrgPath, null);
        request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("access_token", session.AccessToken)]);

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_refresh_token_cookie_is_not_a_bearer_token()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        using var response = await MeAsync(session.RefreshToken);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }
}
