using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class LogoutTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public async Task Logout_returns_204_clears_the_cookie_and_ends_the_session()   // criterion 5
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        using var response = await SessionApi.Logout(Client, login.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        var cookie = SessionApi.RefreshCookieOf(response);
        Assert.Equal(string.Empty, cookie.Value.Value);
        Assert.Equal(TimeSpan.Zero, cookie.MaxAge);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.Equal("/auth", cookie.Path.Value);

        using var refresh = await SessionApi.Refresh(Client, login.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refresh);
    }

    [Fact]
    public async Task Logout_with_a_consumed_token_still_ends_the_family()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        var current = await SessionApi.RefreshOk(Client, login.RefreshToken);

        using var response = await SessionApi.Logout(Client, login.RefreshToken);   // a stale tab logs out
        using var refresh = await SessionApi.Refresh(Client, current.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await SessionApi.AssertInvalidGrantAsync(refresh);
    }

    public static TheoryData<string?> InvalidCookieValues => new()
    {
        (string?)null,               // no cookie at all
        "",
        "not-a-real-reference-0123456789abcdef",
        "%00",
        "aaa.bbb.ccc",
        new string('x', 4000),
    };

    [Theory]
    [MemberData(nameof(InvalidCookieValues))]
    public async Task Logout_without_a_valid_cookie_still_returns_204(string? value)   // criterion 6, Review Focus #2
    {
        using var response = await SessionApi.Logout(Client, value);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(TimeSpan.Zero, SessionApi.RefreshCookieOf(response).MaxAge);
    }

    [Fact]
    public async Task Logout_is_indistinguishable_with_and_without_a_session()   // criterion 6: no enumeration
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        using var real = await SessionApi.Logout(Client, login.RefreshToken);
        using var again = await SessionApi.Logout(Client, login.RefreshToken);   // idempotent
        using var none = await SessionApi.Logout(Client, refreshToken: null);

        foreach (var response in new[] { again, none })
        {
            Assert.Equal(real.StatusCode, response.StatusCode);
            Assert.Equal(LoginApi.HeaderNames(real), LoginApi.HeaderNames(response));
            Assert.Equal(real.Headers.GetValues("Set-Cookie"), response.Headers.GetValues("Set-Cookie"));
        }
    }

    [Fact]
    public async Task Logout_leaves_another_session_alone()   // Decision 7, Review Focus #4
    {
        var first = await SessionApi.LoginAsync(Client, Factory);
        var second = await SessionApi.LoginAsync(Client, Factory);

        using var response = await SessionApi.Logout(Client, first.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await SessionApi.RefreshOk(Client, second.RefreshToken);
    }

    [Fact]
    public async Task Revoked_token_gets_the_uniform_401()   // criterion 7: "revoked"
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        using var logout = await SessionApi.Logout(Client, login.RefreshToken);

        using var revoked = await SessionApi.Refresh(Client, login.RefreshToken);
        using var missing = await SessionApi.Refresh(Client, refreshToken: null);

        await SessionApi.AssertInvalidGrantAsync(revoked);
        Assert.Equal(missing.Content.Headers.ContentType, revoked.Content.Headers.ContentType);
        Assert.Equal(LoginApi.HeaderNames(missing), LoginApi.HeaderNames(revoked));
    }

    [Fact]
    public async Task Trailing_slash_path_logs_out_too()   // Review Focus #1
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        using var response = await SessionApi.Logout(Client, login.RefreshToken, SessionApi.LogoutPath + "/");
        using var refresh = await SessionApi.Refresh(Client, login.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await SessionApi.AssertInvalidGrantAsync(refresh);
    }
}
