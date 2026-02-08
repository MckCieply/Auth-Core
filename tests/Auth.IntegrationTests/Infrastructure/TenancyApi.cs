using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Drives the endpoints of spec 0005 over HTTP with a bearer token, and asserts their response contracts.</summary>
public static class TenancyApi
{
    public static HttpRequestMessage Request(HttpMethod method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = Request(method, path, token, body);
        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> Get(HttpClient client, string path, string? token) => Send(client, HttpMethod.Get, path, token);

    public const string PreviewPath = "/auth/invites/preview";
    public const string AcceptPath = "/auth/invites/accept";

    public static Task<HttpResponseMessage> Preview(HttpClient client, string token) =>
        client.PostAsJsonAsync(PreviewPath, new { token });

    public static Task<HttpResponseMessage> Accept(HttpClient client, string token, string password) =>
        client.PostAsJsonAsync(AcceptPath, new { token, password });

    /// <summary>Asserts the status, that the answer is never stored and sets no cookie, and returns the raw body.</summary>
    public static async Task<string> ReadAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {raw}");
        AccountApi.AssertNeverStoredAndNoCookie(response);
        return raw;
    }

    /// <summary>A <c>200</c> with a JSON body; returns it.</summary>
    public static async Task<JsonElement> ReadOkAsync(HttpResponseMessage response)
    {
        var raw = await ReadAsync(response, HttpStatusCode.OK);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    /// <summary>A <c>201</c> with a JSON body; returns it.</summary>
    public static async Task<JsonElement> ReadCreatedAsync(HttpResponseMessage response)
    {
        var raw = await ReadAsync(response, HttpStatusCode.Created);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    /// <summary>The error of the contract: this status and exactly <c>{"error":"code"}</c>.</summary>
    public static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await ReadAsync(response, status);
        Assert.Equal($$"""{"error":"{{code}}"}""", raw);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>A <c>202</c> or <c>204</c>: no body.</summary>
    public static async Task AssertEmptyAsync(HttpResponseMessage response, HttpStatusCode status) =>
        Assert.Equal("", await ReadAsync(response, status));

    /// <summary>The <c>401</c> of a missing or invalid token: an empty body and a bearer challenge, never stored.</summary>
    public static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"Expected 401, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("", raw);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }
}
