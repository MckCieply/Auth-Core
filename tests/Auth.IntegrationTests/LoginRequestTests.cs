using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class LoginRequestTests : IAsyncLifetime
{
    private const string LoginPath = "/auth/login";

    private readonly AuthAppFactory _factory;
    private HttpClient _client = null!;

    public LoginRequestTests(PostgresFixture postgres, KeyMaterialFixture keys)
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

    [Theory]
    [InlineData("""{"password":"x"}""")]                              // missing email
    [InlineData("""{"email":"a@example.com"}""")]                     // missing password
    [InlineData("""{"email":"a@example.com","password":""}""")]       // blank password
    [InlineData("""{"email":"a@example.com","password":"   "}""")]    // whitespace password
    [InlineData("""{"email":"  ","password":"x"}""")]                 // whitespace email
    [InlineData("""{"email":123,"password":"x"}""")]                  // Review Focus #2: wrong type
    [InlineData("""{"email":"a@example.com","password":null}""")]
    [InlineData("""["a@example.com","x"]""")]                         // non-object root
    [InlineData("""{}""")]
    [InlineData("""not json""")]
    [InlineData("")]                                                  // empty body
    [InlineData("""{"email":"a@example.com","password":"\ud800"}""")] // lone surrogate escape
    [InlineData("null")]                                              // JSON null root
    [InlineData("""{"email":"a@example.com","password":"x",""")]      // truncated
    [InlineData("""{"email":"a\u0000b","password":"x"}""")]          // NUL in email (Npgsql 22021 -> was a 500)
    [InlineData("""{"email":"a@example.com","password":"x\u0000y"}""")] // NUL in password
    [InlineData("""{"email":"a@exam\u0007ple.com","password":"x"}""")] // other C0 control char in email
    [InlineData("""{"email":"a@example.com\n","password":"x"}""")]   // trailing newline in email
    public async Task Malformed_json_login_returns_400(string body)
    {
        var response = await _client.PostAsync(LoginPath, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertInvalidRequestBodyAsync(response);
    }

    [Fact]
    public async Task Form_encoded_oidc_request_returns_400()   // Review Focus #3
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = _factory.SeedEmail,
            ["password"] = _factory.SeedPassword,
        });

        var response = await _client.PostAsync(LoginPath, form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertInvalidRequestBodyAsync(response);
    }

    [Fact]
    public async Task Plain_text_body_returns_400()
    {
        var response = await _client.PostAsync(LoginPath, new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertInvalidRequestBodyAsync(response);
    }

    [Fact]
    public async Task Oversized_body_returns_400()
    {
        var big = $$"""{"email":"a@example.com","password":"{{new string('x', 9000)}}"}""";

        var response = await _client.PostAsync(LoginPath, new StringContent(big, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertInvalidRequestBodyAsync(response);
    }

    [Fact]
    public async Task Well_formed_json_login_is_not_rejected_as_malformed()
    {
        var body = $$"""{"email":"{{_factory.SeedEmail}}","password":"wrong"}""";

        var response = await _client.PostAsync(LoginPath, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Json_with_charset_is_accepted()
    {
        var body = $$"""{"email":"{{_factory.SeedEmail}}","password":"wrong"}""";
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };

        var response = await _client.PostAsync(LoginPath, content);

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Error_response_never_echoes_submitted_values()
    {
        const string body = """{"email":"leak-me@example.com","password":"   ","extra":"leak-me-too"}""";

        var response = await _client.PostAsync(LoginPath, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("leak-me", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // OpenIddict matches the token endpoint with or without a trailing slash, so /auth/login/ must behave exactly
    // like /auth/login (it used to leave the request null and crash with a 500 inside OpenIddict).
    private const string LoginPathWithSlash = "/auth/login/";

    [Fact]
    public async Task Trailing_slash_login_with_valid_credentials_returns_200_with_exact_contract_body()
    {
        using var response = await _client.PostAsJsonAsync(
            LoginPathWithSlash, new { email = _factory.SeedEmail, password = _factory.SeedPassword });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)response.StatusCode}: {raw}");
        using var json = JsonDocument.Parse(raw);
        Assert.Equal("authenticated", json.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("access_token").GetString()));
        Assert.Equal(["access_token", "status"], json.RootElement.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Trailing_slash_login_with_wrong_password_returns_the_uniform_401()
    {
        using var response = await _client.PostAsJsonAsync(
            LoginPathWithSlash, new { email = _factory.SeedEmail, password = "definitely-wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Trailing_slash_login_with_non_json_body_returns_400()
    {
        using var response = await _client.PostAsync(LoginPathWithSlash, new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertInvalidRequestBodyAsync(response);
    }

    [Fact]
    public async Task Trailing_slash_login_with_get_returns_400()
    {
        using var response = await _client.GetAsync(LoginPathWithSlash);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertInvalidRequestBodyAsync(response);
    }

    private static async Task AssertInvalidRequestBodyAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(raw);
        Assert.Equal("invalid_request", json.RootElement.GetProperty("error").GetString());
    }
}
