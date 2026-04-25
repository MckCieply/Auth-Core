using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Drives the provider endpoints of spec 0011 as a browser and a product's screens would, carrying the cookies by hand.</summary>
public static class ProviderApi
{
    /// <summary>The start of OpenIddict's request-forgery cookie: its name ends with a value of each round trip.</summary>
    public const string StateCookiePrefix = "OpenIddict.Client.State.";

    /// <summary>The cookie that binds a ticket to the browser of its round trip.</summary>
    public const string BindingCookie = "auth_pt";

    /// <summary>The return screen of the test hosts: every callback answers a redirect to it.</summary>
    public const string ReturnUrl = AuthAppFactory.DefaultProviderReturnUrl;

    /// <summary>A ticket of a callback: the value of the fragment, its purpose, and the value of the <c>auth_pt</c> cookie set with it.</summary>
    public sealed record Ticket(string Value, string Purpose, string Binding);

    /// <summary>Calls the callback as the provider's redirect makes a browser do, with the cookie line given (the request-forgery cookie).</summary>
    public static async Task<HttpResponseMessage> CallbackAsync(
        HttpClient browser, string pathAndQuery, string? cookie, Action<HttpRequestMessage>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(browser);

        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        configure?.Invoke(request);
        return await browser.SendAsync(request);
    }

    /// <summary>The members of the fragment of a callback's redirect to the return screen; asserts the <c>302</c> to that screen.</summary>
    public static IReadOnlyDictionary<string, string> FragmentOf(HttpResponseMessage callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        var location = callback.Headers.Location?.OriginalString ?? throw new InvalidOperationException("The callback has no Location.");
        Assert.StartsWith(ReturnUrl + "#", location, StringComparison.Ordinal);
        return QueryHelpers.ParseQuery(location[(ReturnUrl.Length + 1)..]).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
    }

    /// <summary>The <c>auth_pt</c> cookie a response sets (or clears); <see langword="null"/> when it sets none.</summary>
    public static SetCookieHeaderValue? BindingCookieOf(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? SetCookieHeaderValue.ParseList([.. values]).SingleOrDefault(c => c.Name == BindingCookie)
            : null;
    }

    /// <summary>The ticket and its binding of a successful callback.</summary>
    public static Ticket TicketOf(HttpResponseMessage callback)
    {
        var fragment = FragmentOf(callback);
        Assert.True(fragment.TryGetValue("ticket", out var ticket), "The callback answered: " + string.Join('&', fragment.Select(p => p.Key + "=" + p.Value)));
        var cookie = BindingCookieOf(callback) ?? throw new InvalidOperationException("The callback set no auth_pt.");
        return new Ticket(ticket, fragment["purpose"], cookie.Value.Value!);
    }

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
