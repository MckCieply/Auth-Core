using System.Net;
using System.Text.Json;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Auth.IntegrationTests;

public sealed class LoginCookieTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public async Task Login_sets_the_refresh_cookie_with_adr_0004_attributes()   // criterion 8
    {
        using var response = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

        Assert.Single(response.Headers.GetValues("Set-Cookie"));
        var cookie = SessionApi.RefreshCookieOf(response);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.Equal("/auth", cookie.Path.Value);
        Assert.Equal(TimeSpan.FromDays(14), cookie.MaxAge);
        Assert.False(cookie.Domain.HasValue);
        Assert.Null(cookie.Expires);
    }

    [Fact]
    public async Task Login_body_is_unchanged_and_never_carries_the_refresh_token()   // criterion 8, Decision 16
    {
        using var response = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);
        var cookie = SessionApi.RefreshCookieOf(response);
        var raw = await response.Content.ReadAsStringAsync();

        using var body = JsonDocument.Parse(raw);
        Assert.Equal(["access_token", "status"], body.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain(cookie.Value.Value!, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_cookie_is_an_opaque_reference_with_a_stored_family()   // Decision 9, spec "To verify"
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        Assert.InRange(session.RefreshToken.Length, 32, 128);
        Assert.DoesNotContain('.', session.RefreshToken);   // not a JWT/JWE

        using var scope = Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var entry = await tokens.FindByReferenceIdAsync(session.RefreshToken);
        Assert.NotNull(entry);
        Assert.Equal(OpenIddictConstants.TokenTypeIdentifiers.RefreshToken, await tokens.GetTypeAsync(entry));
        Assert.False(string.IsNullOrEmpty(await tokens.GetAuthorizationIdAsync(entry)), "The refresh token has no authorization (family).");
    }

    [Fact]
    public async Task Failed_login_sets_no_cookie()
    {
        using var response = await LoginApi.Login(Client, Factory.SeedEmail, "definitely-wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
}
