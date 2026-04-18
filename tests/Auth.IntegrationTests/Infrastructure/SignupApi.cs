using System.Net;
using System.Net.Http.Json;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Drives the four endpoints of specs 0009 and 0010 over HTTP.</summary>
public static class SignupApi
{
    public const string OptionsPath = "/auth/options";
    public const string RequestPath = "/auth/signup";
    public const string PreviewPath = "/auth/signup/preview";
    public const string CompletePath = "/auth/signup/complete";

    public const string Disabled = """{"error":"signup_disabled"}""";
    public const string AlreadyRegistered = """{"error":"already_registered"}""";

    public static Task<HttpResponseMessage> Options(HttpClient client) => client.GetAsync(OptionsPath);

    public static Task<HttpResponseMessage> Request(HttpClient client, string email, string companyName) =>
        client.PostAsJsonAsync(RequestPath, new { email, company_name = companyName });

    public static Task<HttpResponseMessage> Preview(HttpClient client, string token) =>
        client.PostAsJsonAsync(PreviewPath, new { token });

    public static Task<HttpResponseMessage> Complete(HttpClient client, string token, string password, string companyName) =>
        client.PostAsJsonAsync(CompletePath, new { token, password, company_name = companyName });

    /// <summary>A private sign-up (spec 0010): the address and the kind, no company name.</summary>
    public static Task<HttpResponseMessage> RequestPrivate(HttpClient client, string email) =>
        client.PostAsJsonAsync(RequestPath, new { email, kind = "private" });

    /// <summary>The completion of a private sign-up (spec 0010): the token and the password, no company name.</summary>
    public static Task<HttpResponseMessage> CompletePrivate(HttpClient client, string token, string password) =>
        client.PostAsJsonAsync(CompletePath, new { token, password });

    /// <summary>The <c>403</c> of a sign-up that is off: the exact body, never stored, no cookie.</summary>
    public static async Task AssertDisabledAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"Expected 403, got {(int)response.StatusCode}: {raw}");
        Assert.Equal(Disabled, raw);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }

    /// <summary>The <c>409 already_registered</c>: the exact body, never stored, no cookie.</summary>
    public static async Task AssertAlreadyRegisteredAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"Expected 409, got {(int)response.StatusCode}: {raw}");
        Assert.Equal(AlreadyRegistered, raw);
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }
}
