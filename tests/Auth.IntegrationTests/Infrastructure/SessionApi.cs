using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Net.Http.Headers;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Drives login, refresh and logout the way a browser would, but carries the cookie by hand: the cookie is
/// <c>Secure</c> and the test server speaks plain HTTP, so an automatic cookie container would never send it.
/// </summary>
public static class SessionApi
{
    public const string RefreshPath = "/auth/refresh";
    public const string LogoutPath = "/auth/logout";
    public const string SwitchPath = "/auth/switch";
    public const string CookieName = "auth_rt";

    public sealed record Session(string AccessToken, string RefreshToken, SetCookieHeaderValue Cookie);

    public static HttpClient CreateClient(AuthAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    public static SetCookieHeaderValue RefreshCookieOf(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values), "The response has no Set-Cookie header.");
        return Assert.Single(SetCookieHeaderValue.ParseList(values.ToList()), c => c.Name == CookieName);
    }

    public static async Task<Session> LoginAsync(HttpClient client, AuthAppFactory factory)
    {
        using var response = await LoginApi.Login(client, factory.SeedEmail, factory.SeedPassword);
        return await ReadSessionAsync(response);
    }

    /// <summary>Logs in as any account and returns its session; asserts the <c>200</c>.</summary>
    public static async Task<Session> LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await LoginApi.Login(client, email, password);
        return await ReadSessionAsync(response);
    }

    public static Task<HttpResponseMessage> Refresh(HttpClient client, string? refreshToken, string path = RefreshPath) =>
        client.SendAsync(WithCookie(new HttpRequestMessage(HttpMethod.Post, path), refreshToken));

    public static async Task<Session> RefreshOk(HttpClient client, string refreshToken)
    {
        using var response = await Refresh(client, refreshToken);
        return await ReadSessionAsync(response);
    }

    /// <summary><c>POST /auth/switch</c> with the cookie as <see cref="Refresh"/> sends it and <paramref name="body"/> as given, declared as <paramref name="contentType"/>.</summary>
    public static Task<HttpResponseMessage> Switch(HttpClient client, string? refreshToken, string body, string contentType = "application/json", string path = SwitchPath)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8) };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        return client.SendAsync(WithCookie(request, refreshToken));
    }

    /// <summary>Switches to the space <paramref name="orgId"/>: the body is <c>{"org_id":"&lt;id&gt;"}</c>.</summary>
    public static Task<HttpResponseMessage> SwitchTo(HttpClient client, string? refreshToken, Guid orgId) =>
        Switch(client, refreshToken, $$"""{"org_id":"{{orgId:D}}"}""");

    public static Task<HttpResponseMessage> Logout(HttpClient client, string? refreshToken, string path = LogoutPath) =>
        client.SendAsync(WithCookie(new HttpRequestMessage(HttpMethod.Post, path), refreshToken));

    /// <summary>The one refresh failure: 401, the exact body, and the cookie left alone.</summary>
    public static async Task AssertInvalidGrantAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"Expected 401, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("""{"error":"invalid_grant"}""", raw);
        Assert.False(response.Headers.Contains("Set-Cookie"), "A failed refresh must not write the cookie.");
    }

    private static HttpRequestMessage WithCookie(HttpRequestMessage request, string? refreshToken)
    {
        if (refreshToken is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{CookieName}={refreshToken}");
        }

        return request;
    }

    public static async Task<Session> ReadSessionAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)response.StatusCode}: {raw}");
        using var body = JsonDocument.Parse(raw);
        var cookie = RefreshCookieOf(response);
        var accessToken = body.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("The response has no access_token.");
        return new Session(accessToken, cookie.Value.Value!, cookie);
    }
}
