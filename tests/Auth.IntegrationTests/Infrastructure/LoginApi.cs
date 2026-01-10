using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Helpers that drive <c>POST /auth/login</c> over HTTP the way a client would.</summary>
public static class LoginApi
{
    public const string Path = "/auth/login";

    /// <summary>Posts the JSON login body and returns the raw response.</summary>
    public static Task<HttpResponseMessage> Login(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync(Path, new { email, password });

    /// <summary>Logs in, asserts <c>200 OK</c> and returns the parsed response body.</summary>
    public static async Task<JsonElement> LoginOk(HttpClient client, string email, string password)
    {
        using var response = await Login(client, email, password);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200 from {Path}, got {(int)response.StatusCode}: {raw}");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    /// <summary>Logs in as the seeded user and returns the <c>access_token</c> JWT.</summary>
    public static async Task<string> LoginToken(HttpClient client, AuthAppFactory factory)
    {
        var body = await LoginOk(client, factory.SeedEmail, factory.SeedPassword);
        return body.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("The login response has no access_token.");
    }

    /// <summary>
    /// Sorted names of all response and content headers, <c>Date</c> excluded (it differs between any two
    /// responses). Two responses with equal names, status and content type are indistinguishable by header shape.
    /// </summary>
    public static IReadOnlyList<string> HeaderNames(HttpResponseMessage response) =>
        response.Headers.Select(h => h.Key)
            .Concat(response.Content.Headers.Select(h => h.Key))
            .Where(name => !string.Equals(name, "Date", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
