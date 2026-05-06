using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Auth.Server.Sessions;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Drives the endpoints of spec 0012's settings screen as the browser sends them: the access token, the refresh cookie of the session of this
/// device and, for a ticket, its <c>auth_pt</c>, all carried by hand (the cookies are <c>Secure</c> and the test server speaks plain HTTP).
/// </summary>
public static class SettingsApi
{
    public const string MePath = "/auth/me";
    public const string ConfirmPath = "/auth/account/confirm";
    public const string ConnectPath = "/auth/providers/connect";
    public const string UnlinkPath = "/auth/account/logins/unlink";
    public const string PasswordPath = "/auth/account/password";
    public const string EndConfirmationPath = "/auth/testing/end-confirmation";

    public static HttpRequestMessage Request(
        HttpMethod method, string path, string? accessToken, string? refreshToken, string? binding = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        List<string> cookies = [];
        if (refreshToken is not null)
        {
            cookies.Add($"{SessionApi.CookieName}={refreshToken}");
        }

        if (binding is not null)
        {
            cookies.Add($"{ProviderApi.BindingCookie}={binding}");
        }

        if (cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        }

        return request;
    }

    public static async Task<HttpResponseMessage> Me(HttpClient client, string accessToken, string? refreshToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = Request(HttpMethod.Get, MePath, accessToken, refreshToken);
        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> Me(HttpClient client, SessionApi.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return Me(client, session.AccessToken, session.RefreshToken);
    }

    /// <summary><c>confirmed_until</c> of <c>GET /auth/me</c>, after asserting its <c>200</c>: the end of the window, or null.</summary>
    public static async Task<string?> ConfirmedUntilAsync(HttpClient client, string accessToken, string? refreshToken)
    {
        using var response = await Me(client, accessToken, refreshToken);
        var me = await TenancyApi.ReadOkAsync(response);
        var value = me.GetProperty("confirmed_until");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    public static Task<string?> ConfirmedUntilAsync(HttpClient client, SessionApi.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return ConfirmedUntilAsync(client, session.AccessToken, session.RefreshToken);
    }

    /// <summary>The window as the contract writes it, for an instant of the test clock.</summary>
    public static string Window(DateTimeOffset until) => ConfirmationWindow.Format(until);

    public static async Task<HttpResponseMessage> PostAs(
        HttpClient client, string path, string? accessToken, string? refreshToken, object? body, string? binding = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = Request(HttpMethod.Post, path, accessToken, refreshToken, binding, body is null ? null : JsonContent.Create(body));
        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> Post(HttpClient client, string path, SessionApi.Session session, object? body, string? binding = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        return PostAs(client, path, session.AccessToken, session.RefreshToken, body, binding);
    }

    /// <summary>A body as it is (for the bodies a serializer would not write), with the session's token and cookie.</summary>
    public static async Task<HttpResponseMessage> PostRaw(HttpClient client, string path, SessionApi.Session session, string body, string? binding = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(session);

        using var request = Request(
            HttpMethod.Post, path, session.AccessToken, session.RefreshToken, binding, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> Confirm(HttpClient client, SessionApi.Session session, object body, string? binding = null) =>
        Post(client, ConfirmPath, session, body, binding);

    public static Task<HttpResponseMessage> ConfirmWithTicket(HttpClient client, SessionApi.Session session, ProviderApi.Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return Post(client, ConfirmPath, session, new { provider_ticket = ticket.Value }, ticket.Binding);
    }

    public static Task<HttpResponseMessage> Connect(HttpClient client, SessionApi.Session session, ProviderApi.Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return Post(client, ConnectPath, session, new { ticket = ticket.Value }, ticket.Binding);
    }

    public static Task<HttpResponseMessage> Unlink(HttpClient client, SessionApi.Session session, string provider) =>
        Post(client, UnlinkPath, session, new { provider });

    public static Task<HttpResponseMessage> ChangePassword(HttpClient client, SessionApi.Session session, string current, string next) =>
        Post(client, PasswordPath, session, new { current_password = current, new_password = next });

    public static Task<HttpResponseMessage> EndConfirmation(HttpClient client, SessionApi.Session session) =>
        Post(client, EndConfirmationPath, session, null);
}
