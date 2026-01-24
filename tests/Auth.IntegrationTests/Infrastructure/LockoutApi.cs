using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Helpers for the lockout tests: failed logins at a human pace, and the 429 contract.</summary>
public static class LockoutApi
{
    public const string WrongPassword = "definitely-wrong";

    /// <summary>Wide enough that the burst rule (five failed attempts in 10 s) never fires.</summary>
    public static readonly TimeSpan HumanPace = TimeSpan.FromSeconds(3);

    /// <summary>Sends <paramref name="times"/> failed logins, each 3 s of host clock after the last, and asserts every one gets the ordinary 401.</summary>
    public static async Task FailAsync(HttpClient client, FakeTimeProvider clock, string email, int times)
    {
        for (var i = 0; i < times; i++)
        {
            clock.Advance(HumanPace);
            using var response = await LoginApi.Login(client, email, WrongPassword);
            var raw = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"Attempt {i + 1}: expected 401, got {(int)response.StatusCode}: {raw}");
        }
    }

    /// <summary>Asserts the whole lockout contract of spec 0003 and returns <c>retry_after_seconds</c>.</summary>
    public static async Task<int> AssertLockedAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.TooManyRequests, $"Expected 429, got {(int)response.StatusCode}: {raw}");

        using var body = JsonDocument.Parse(raw);
        var seconds = body.RootElement.GetProperty("retry_after_seconds").GetInt32();
        Assert.Equal($$"""{"error":"too_many_attempts","retry_after_seconds":{{seconds}}}""", raw);
        Assert.True(seconds >= 1, $"retry_after_seconds must be at least 1, got {seconds}.");
        Assert.Equal(TimeSpan.FromSeconds(seconds), response.Headers.RetryAfter?.Delta);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");
        Assert.Contains(response.Headers.Pragma, p => p.Name == "no-cache");
        Assert.False(response.Headers.Contains("Set-Cookie"), "A refused attempt must not write the cookie.");
        return seconds;
    }
}
