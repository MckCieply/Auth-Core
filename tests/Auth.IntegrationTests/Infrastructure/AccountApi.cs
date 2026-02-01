using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Drives the four account endpoints of spec 0004 over HTTP and asserts their response contracts.</summary>
public static class AccountApi
{
    public const string ForgotPath = "/auth/password/forgot";
    public const string VerifyRequestPath = "/auth/email/verify/request";
    public const string ResetPath = "/auth/password/reset";
    public const string VerifyPath = "/auth/email/verify";

    public const string InvalidRequest = """{"error":"invalid_request"}""";
    public const string InvalidToken = """{"error":"invalid_token"}""";

    public static Task<HttpResponseMessage> Forgot(HttpClient client, string email) =>
        client.PostAsJsonAsync(ForgotPath, new { email });

    public static Task<HttpResponseMessage> RequestVerification(HttpClient client, string email) =>
        client.PostAsJsonAsync(VerifyRequestPath, new { email });

    public static Task<HttpResponseMessage> Reset(HttpClient client, string token, string newPassword) =>
        client.PostAsJsonAsync(ResetPath, new { token, new_password = newPassword });

    public static Task<HttpResponseMessage> Verify(HttpClient client, string token) =>
        client.PostAsJsonAsync(VerifyPath, new { token });

    public static Task<HttpResponseMessage> PostRaw(HttpClient client, string path, string body, string contentType = "application/json") =>
        client.PostAsync(path, new StringContent(body, Encoding.UTF8, contentType));

    /// <summary>A 202 or 204 of the contract: no body, never cached, no cookie.</summary>
    public static async Task AssertEmptyAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("", raw);
        AssertNeverStoredAndNoCookie(response);
    }

    /// <summary>A 400 of the contract with exactly this body.</summary>
    public static async Task AssertErrorAsync(HttpResponseMessage response, string expectedBody)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}: {raw}");
        Assert.Equal(expectedBody, raw);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AssertNeverStoredAndNoCookie(response);
    }

    public static void AssertNeverStoredAndNoCookie(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");
        Assert.Contains(response.Headers.Pragma, p => p.Name == "no-cache");
        Assert.False(response.Headers.Contains("Set-Cookie"), "An account endpoint must not write a cookie.");
    }
}
