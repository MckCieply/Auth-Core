using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.IntegrationTests;

public sealed class RefreshTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public async Task Refresh_returns_a_new_access_token_and_a_rotated_cookie()   // criteria 1, 8
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        using var response = await SessionApi.Refresh(Client, login.RefreshToken);
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(raw);
        Assert.Equal(["access_token"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");

        var cookie = SessionApi.RefreshCookieOf(response);
        Assert.NotEqual(login.RefreshToken, cookie.Value.Value);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.Equal("/auth", cookie.Path.Value);
        Assert.DoesNotContain(cookie.Value.Value!, raw, StringComparison.Ordinal);

        var accessToken = body.RootElement.GetProperty("access_token").GetString()!;
        Assert.NotEqual(login.AccessToken, accessToken);
        var result = await Jwks.ValidateAsync(accessToken, await Jwks.FetchAsync(Client), new TokenOptions());
        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal((await Factory.SeedUserIdAsync()).ToString(), new JsonWebToken(accessToken).Subject);
    }

    [Fact]
    public async Task Refreshed_access_token_has_the_login_claim_set()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        var refreshed = await SessionApi.RefreshOk(Client, login.RefreshToken);

        static string[] ClaimTypes(string jwt) =>
            new JsonWebToken(jwt).Claims.Select(c => c.Type).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(ClaimTypes(login.AccessToken), ClaimTypes(refreshed.AccessToken));
        Assert.Equal("at+jwt", new JsonWebToken(refreshed.AccessToken).Typ);
    }

    [Fact]
    public async Task Rotation_stays_in_one_family_and_can_repeat()   // spec "To verify": family
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        var first = await SessionApi.RefreshOk(Client, login.RefreshToken);
        var second = await SessionApi.RefreshOk(Client, first.RefreshToken);
        var third = await SessionApi.RefreshOk(Client, second.RefreshToken);

        using var scope = Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        async Task<string?> FamilyOf(string reference) =>
            await tokens.GetAuthorizationIdAsync((await tokens.FindByReferenceIdAsync(reference))!);
        var family = await FamilyOf(login.RefreshToken);
        Assert.False(string.IsNullOrEmpty(family));
        Assert.Equal(family, await FamilyOf(first.RefreshToken));
        Assert.Equal(family, await FamilyOf(third.RefreshToken));
    }

    [Fact]
    public async Task Every_refresh_failure_returns_the_same_401()   // criterion 7
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        using var missing = await SessionApi.Refresh(Client, refreshToken: null);
        using var empty = await SessionApi.Refresh(Client, "");
        using var unknown = await SessionApi.Refresh(Client, "not-a-real-reference-0123456789abcdef");

        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(Factory.SeedEmail);
            Assert.True((await users.DeleteAsync(user!)).Succeeded);
        }

        using var userGone = await SessionApi.Refresh(Client, login.RefreshToken);   // Decision 11

        foreach (var response in new[] { missing, empty, unknown, userGone })
        {
            await SessionApi.AssertInvalidGrantAsync(response);
            Assert.Equal(missing.Content.Headers.ContentType, response.Content.Headers.ContentType);
            Assert.Equal(LoginApi.HeaderNames(missing), LoginApi.HeaderNames(response));
        }
    }

    public static TheoryData<string> HostileCookieValues => new()
    {
        "%00",                       // NUL
        "a%0D%0Ab",                  // CR LF
        "%22quoted%22",
        "aaa.bbb.ccc",               // JWT-shaped
        new string('x', 4000),       // far past any token length
    };

    [Theory]
    [MemberData(nameof(HostileCookieValues))]
    public async Task Hostile_cookie_value_returns_401_never_500(string value)   // Review Focus #2
    {
        using var response = await SessionApi.Refresh(Client, value);

        await SessionApi.AssertInvalidGrantAsync(response);
    }

    [Fact]
    public async Task Access_token_in_the_cookie_is_not_a_refresh_token()   // Review Focus #2
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        using var response = await SessionApi.Refresh(Client, login.AccessToken);

        await SessionApi.AssertInvalidGrantAsync(response);
    }

    [Fact]
    public async Task Request_body_cannot_stand_in_for_the_cookie()   // Review Focus #3
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = Factory.SeedEmail,
            ["password"] = Factory.SeedPassword,
            ["refresh_token"] = login.RefreshToken,
        });
        using var json = JsonContent.Create(new { email = Factory.SeedEmail, password = Factory.SeedPassword, refresh_token = login.RefreshToken });

        using var viaForm = await Client.PostAsync(SessionApi.RefreshPath, form);
        using var viaJson = await Client.PostAsync(SessionApi.RefreshPath, json);

        await SessionApi.AssertInvalidGrantAsync(viaForm);
        await SessionApi.AssertInvalidGrantAsync(viaJson);
    }

    [Fact]
    public async Task Request_body_cannot_override_the_cookie()   // Review Focus #3
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, SessionApi.RefreshPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = "not-a-real-reference-0123456789abcdef",
            }),
        };
        request.Headers.TryAddWithoutValidation("Cookie", $"{SessionApi.CookieName}={login.RefreshToken}");

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // the cookie decided; the body was never read
    }

    [Fact]
    public async Task Get_returns_400_invalid_request()   // Decision 15
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, SessionApi.RefreshPath);
        request.Headers.TryAddWithoutValidation("Cookie", $"{SessionApi.CookieName}={login.RefreshToken}");

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request", body.RootElement.GetProperty("error").GetString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Trailing_slash_path_refreshes_too()   // Review Focus #1
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        using var ok = await SessionApi.Refresh(Client, login.RefreshToken, SessionApi.RefreshPath + "/");
        using var failed = await SessionApi.Refresh(Client, refreshToken: null, SessionApi.RefreshPath + "/");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.Equal(["access_token"], body.RootElement.EnumerateObject().Select(p => p.Name));
        await SessionApi.AssertInvalidGrantAsync(failed);
    }

    [Fact]
    public async Task Login_path_ignores_the_refresh_cookie()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        using var response = await SessionApi.Refresh(Client, login.RefreshToken, LoginApi.Path);   // cookie, no body

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
