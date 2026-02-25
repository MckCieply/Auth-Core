using System.Net;
using System.Text;
using System.Text.Json;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Sends requests as a client at a given address, and asserts the <c>429 too_many_requests</c> of spec 0008.</summary>
public static class RateLimitApi
{
    /// <summary>A request from <paramref name="remote"/>, optionally behind a proxy that says <paramref name="forwardedFor"/>.</summary>
    public static HttpRequestMessage Request(
        HttpMethod method, string path, string? remote = null, string? forwardedFor = null, string? body = "{}")
    {
        var request = new HttpRequestMessage(method, path);
        if (remote is not null)
        {
            request.Headers.Add(AuthAppFactory.RemoteAddressHeader, remote);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        if (body is not null && method != HttpMethod.Get && method != HttpMethod.Head)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? remote = null, string? forwardedFor = null, string? body = "{}")
    {
        using var request = Request(method, path, remote, forwardedFor, body);
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Asserts the whole contract of the <c>429</c>: the exact body, <c>Retry-After</c> with the same number, <c>no-store</c> and no
    /// cookie. Returns <c>retry_after_seconds</c>.
    /// </summary>
    public static async Task<int> AssertTooManyRequestsAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.TooManyRequests, $"Expected 429, got {(int)response.StatusCode}: {raw}");

        using var body = JsonDocument.Parse(raw);
        var seconds = body.RootElement.GetProperty("retry_after_seconds").GetInt32();
        Assert.Equal($$"""{"error":"too_many_requests","retry_after_seconds":{{seconds}}}""", raw);
        Assert.True(seconds >= 1, $"retry_after_seconds must be at least 1, got {seconds}.");
        Assert.Equal(TimeSpan.FromSeconds(seconds), response.Headers.RetryAfter?.Delta);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");
        Assert.Contains(response.Headers.Pragma, p => p.Name == "no-cache");
        Assert.False(response.Headers.Contains("Set-Cookie"), "A refused request must not write a cookie.");
        return seconds;
    }
}
