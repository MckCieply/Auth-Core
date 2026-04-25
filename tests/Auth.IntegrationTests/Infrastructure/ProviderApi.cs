using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Drives the provider endpoints of spec 0011 as a browser and a product's screens would, carrying the cookies by hand.</summary>
public static class ProviderApi
{
    /// <summary>The start of OpenIddict's request-forgery cookie: its name ends with a value of each round trip.</summary>
    public const string StateCookiePrefix = "OpenIddict.Client.State.";

    /// <summary>What the start answered: the redirect to the provider, its query, and the request-forgery cookie as a browser sends it back.</summary>
    public sealed record Start(Uri Location, IReadOnlyDictionary<string, string> Query, string StateCookie, SetCookieHeaderValue StateCookieHeader);

    /// <summary>Starts a round trip and asserts the <c>302</c>.</summary>
    public static async Task<Start> StartAsync(HttpClient browser, string provider, string query = "", Action<HttpRequestMessage>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(browser);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/auth/providers/{provider}/start{query}");
        configure?.Invoke(request);
        using var response = await browser.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Found, $"Expected 302, got {(int)response.StatusCode}: {raw}");
        var location = response.Headers.Location ?? throw new InvalidOperationException("The start has no Location.");
        var cookie = Assert.Single(
            SetCookieHeaderValue.ParseList([.. response.Headers.GetValues("Set-Cookie")]),
            c => c.Name.Value!.StartsWith(StateCookiePrefix, StringComparison.Ordinal));
        return new Start(
            location,
            QueryHelpers.ParseQuery(location.Query).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal),
            $"{cookie.Name}={cookie.Value}",
            cookie);
    }
}
