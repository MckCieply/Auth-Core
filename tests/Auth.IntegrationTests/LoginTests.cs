using System.Net;
using System.Net.Http.Json;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using static Auth.IntegrationTests.Infrastructure.LoginApi;

namespace Auth.IntegrationTests;

public sealed class LoginTests : IAsyncLifetime
{
    private readonly AuthAppFactory _factory;
    private HttpClient _client = null!;

    public LoginTests(PostgresFixture postgres, KeyMaterialFixture keys)
    {
        _factory = new AuthAppFactory(postgres, keys);
    }

    public ValueTask InitializeAsync()
    {
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Valid_credentials_return_200_with_exact_contract_body()   // criterion 1
    {
        var body = await LoginOk(_client, _factory.SeedEmail, _factory.SeedPassword);

        Assert.Equal("authenticated", body.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("access_token").GetString()));
        Assert.Equal(["access_token", "status"], body.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Token_is_unencrypted_RS256_jws_with_kid()   // criterion 2
    {
        var jwt = new JsonWebToken(await LoginToken(_client, _factory));

        Assert.False(jwt.IsEncrypted);
        Assert.Equal("RS256", jwt.Alg);
        Assert.False(string.IsNullOrEmpty(jwt.Kid));
    }

    [Fact]
    public async Task Token_carries_contract_claims_and_nothing_from_week_3()   // criterion 3
    {
        var jwt = new JsonWebToken(await LoginToken(_client, _factory));

        Assert.Equal("http://localhost:8080/auth", jwt.Issuer);
        Assert.Equal(["auth-core-dev"], jwt.Audiences.ToArray());
        Assert.Equal((await _factory.SeedUserIdAsync()).ToString(), jwt.Subject);
        Assert.True(jwt.ValidTo > DateTime.UtcNow);
        Assert.True(jwt.ValidTo - jwt.IssuedAt <= TimeSpan.FromMinutes(10));
        // Bound exp against NOW too, so a trivially short (or overlong) lifetime fails: ~10 min from the login.
        Assert.InRange(jwt.ValidTo, DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow.AddMinutes(10).AddSeconds(30));
        foreach (var absent in new[] { "org_id", "roles", "permissions" })
        {
            Assert.DoesNotContain(jwt.Claims, c => c.Type == absent);
        }
    }

    [Fact]
    public async Task Token_payload_claim_set_is_exactly_the_contract_plus_openiddict_metadata()
    {
        var jwt = new JsonWebToken(await LoginToken(_client, _factory));

        // iss, aud, sub, exp are the contract. iat, jti and oi_tkn_id are OpenIddict metadata that cannot be
        // switched off; they are accepted extras pending an owner decision. Anything else is a contract leak.
        string[] expected = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "sub"];
        Assert.Equal(expected, jwt.Claims.Select(c => c.Type).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Token_header_typ_is_at_jwt_and_payload_has_no_identity_extras()
    {
        var jwt = new JsonWebToken(await LoginToken(_client, _factory));

        Assert.Equal("at+jwt", jwt.Typ);
        foreach (var absent in new[] { "email", "name", "role", "preferred_username", "scope" })
        {
            Assert.DoesNotContain(jwt.Claims, c => c.Type == absent);
        }
    }

    [Fact]
    public async Task Token_validates_with_Jwks_helper()
    {
        var token = await LoginToken(_client, _factory);
        var jwks = await Jwks.FetchAsync(_client);
        var expected = new TokenOptions();

        var result = await Jwks.ValidateAsync(token, jwks, expected);

        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_are_indistinguishable()   // criterion 5 + Review Focus #4
    {
        using var wrongPw = await Login(_client, _factory.SeedEmail, "definitely-wrong");
        using var unknown = await Login(_client, "nobody@example.com", "definitely-wrong");

        foreach (var r in new[] { wrongPw, unknown })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        var wrongPwBody = await wrongPw.Content.ReadAsStringAsync();
        Assert.Equal("""{"error":"invalid_credentials"}""", wrongPwBody);
        Assert.Equal(wrongPwBody, await unknown.Content.ReadAsStringAsync());
        Assert.Equal(wrongPw.Content.Headers.ContentType, unknown.Content.Headers.ContentType);
        Assert.Equal(HeaderNames(wrongPw), HeaderNames(unknown));   // names only; Date differs
    }

    [Fact]
    public async Task Wrong_password_response_has_no_store_cache_header()
    {
        using var response = await Login(_client, _factory.SeedEmail, "definitely-wrong");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");
    }

    [Fact]
    public async Task Email_match_is_case_insensitive()   // Review Focus #1
    {
        await LoginOk(_client, _factory.SeedEmail.ToUpperInvariant(), _factory.SeedPassword);
    }

    [Theory]
    [InlineData("Basic Zm9vOmJhcg==")]       // foo:bar
    [InlineData("Basic !!!not-base64!!!")]   // malformed
    [InlineData("Basic")]                    // no credentials at all
    public async Task Basic_authorization_header_is_ignored_for_valid_credentials(string authorization)
    {
        using var request = JsonLogin(_factory.SeedEmail, _factory.SeedPassword, authorization);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("authenticated", body.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("Basic Zm9vOmJhcg==")]
    [InlineData("Basic !!!not-base64!!!")]
    public async Task Basic_authorization_header_does_not_change_the_uniform_401(string authorization)
    {
        using var request = JsonLogin(_factory.SeedEmail, "definitely-wrong", authorization);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await response.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage JsonLogin(string email, string password, string authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LoginApi.Path)
        {
            Content = JsonContent.Create(new { email, password }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }
}
