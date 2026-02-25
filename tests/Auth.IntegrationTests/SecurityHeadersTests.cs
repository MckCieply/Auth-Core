using System.Net;
using System.Text.RegularExpressions;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class SecurityHeadersTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private static readonly Regex Nonce = new("'nonce-([^']+)'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData("GET", "/auth/health", HttpStatusCode.OK)]
    [InlineData("HEAD", "/auth/health", HttpStatusCode.OK)]
    [InlineData("GET", "/nope", HttpStatusCode.NotFound)]
    [InlineData("GET", "/auth/nope", HttpStatusCode.NotFound)]
    [InlineData("GET", "/auth/logout", HttpStatusCode.MethodNotAllowed)]
    [InlineData("POST", "/auth/login", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/auth/refresh", HttpStatusCode.Unauthorized)]
    [InlineData("POST", "/auth/password/forgot", HttpStatusCode.BadRequest)]
    [InlineData("GET", "/auth/me", HttpStatusCode.Unauthorized)]
    [InlineData("GET", "/auth/org/members", HttpStatusCode.Unauthorized)]
    [InlineData("POST", "/auth/invites/preview", HttpStatusCode.BadRequest)]
    public async Task Every_answer_of_the_pipeline_has_the_headers_the_framework_404_and_405_included(string method, string path, HttpStatusCode status)   // criterion 4
    {
        using var response = await RateLimitApi.SendAsync(Client, new HttpMethod(method), path, body: null);

        Assert.Equal(status, response.StatusCode);
        SecurityHeadersApi.AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task The_key_set_and_the_openapi_document_have_every_header_but_no_cache_control()   // criterion 4
    {
        using var jwks = await Client.GetAsync("/auth/.well-known/jwks.json");
        using var document = await Client.GetAsync("/auth/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, jwks.StatusCode);
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        SecurityHeadersApi.AssertSecurityHeaders(jwks, cacheable: true);
        SecurityHeadersApi.AssertSecurityHeaders(document, cacheable: true);
    }

    [Fact]
    public async Task A_successful_login_has_the_headers_and_still_its_own_cookie()
    {
        using var response = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SecurityHeadersApi.AssertSecurityHeaders(response);
        Assert.True(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task The_interactive_reference_gets_a_policy_of_its_own_with_the_nonce_of_its_script()   // spec 0008 → Security headers
    {
        using var first = await Client.GetAsync("/auth/scalar/");
        var body = await first.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var policy = SecurityHeadersApi.HeaderValue(first, "Content-Security-Policy");
        var nonce = Nonce.Match(policy).Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Equal(
            $"default-src 'none'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
            policy);
        Assert.Contains(nonce, body, StringComparison.Ordinal);   // the one inline script carries it
        Assert.DoesNotContain("fonts.scalar.com", body, StringComparison.OrdinalIgnoreCase);   // the default fonts are off
        Assert.Equal("nosniff", SecurityHeadersApi.HeaderValue(first, "X-Content-Type-Options"));
        Assert.Equal("DENY", SecurityHeadersApi.HeaderValue(first, "X-Frame-Options"));
        Assert.Equal("no-referrer", SecurityHeadersApi.HeaderValue(first, "Referrer-Policy"));

        using var second = await Client.GetAsync("/auth/scalar/");
        var otherNonce = Nonce.Match(SecurityHeadersApi.HeaderValue(second, "Content-Security-Policy")).Groups[1].Value;
        Assert.NotEqual(nonce, otherNonce);   // new for every response
    }

    [Fact]
    public async Task The_policy_of_the_interactive_reference_is_for_its_own_path_only()
    {
        using var health = await Client.GetAsync("/auth/health");

        Assert.Equal(SecurityHeadersApi.StrictPolicy, SecurityHeadersApi.HeaderValue(health, "Content-Security-Policy"));
    }
}
