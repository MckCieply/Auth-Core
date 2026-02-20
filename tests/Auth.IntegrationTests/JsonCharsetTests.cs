using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

public sealed class JsonCharsetTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private const string Utf16 = "application/json; charset=utf-16";
    private const string SomeId = "11111111-1111-1111-1111-111111111111";

    private Task<HttpResponseMessage> SendAsync(string method, string path, string contentType, string body = "{}")
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent(body, Encoding.UTF8) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return Client.SendAsync(request);
    }

    private static async Task AssertUnsupportedAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnsupportedMediaType, $"Expected 415, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("""{"error":"unsupported_media_type"}""", raw);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        SecurityHeadersApi.AssertSecurityHeaders(response);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("POST", "/auth/login")]
    [InlineData("POST", "/auth/password/forgot")]
    [InlineData("POST", "/auth/email/verify/request")]
    [InlineData("POST", "/auth/password/reset")]
    [InlineData("POST", "/auth/email/verify")]
    [InlineData("POST", "/auth/invites/preview")]
    [InlineData("POST", "/auth/invites/accept")]
    [InlineData("PATCH", "/auth/org")]
    [InlineData("POST", "/auth/org/invites")]
    [InlineData("PUT", "/auth/org/members/" + SomeId + "/role")]
    [InlineData("POST", "/auth/org/roles")]
    [InlineData("PUT", "/auth/org/roles/" + SomeId)]
    [InlineData("DELETE", "/auth/org/roles/" + SomeId)]
    public async Task A_body_declared_utf_16_is_a_415_on_every_endpoint_that_reads_one_and_before_authentication(string method, string path)   // criterion 6
    {
        using var response = await SendAsync(method, path, Utf16);   // no token: the 415 comes before the 401

        await AssertUnsupportedAsync(response);
    }

    [Theory]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/json; charset=UTF-16")]
    [InlineData("APPLICATION/JSON;charset=utf-16")]
    [InlineData("application/json; charset=\"utf-16\"")]
    [InlineData("application/json; charset=iso-8859-1")]
    [InlineData("application/json; charset=utf8")]   // not the spelling the contract allows
    [InlineData("application/json; charset=utf-32")]
    public async Task Any_charset_other_than_utf_8_is_refused_in_any_spelling(string contentType)   // criterion 6
    {
        using var response = await SendAsync("POST", "/auth/login", contentType);

        await AssertUnsupportedAsync(response);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; charset=UTF-8")]
    [InlineData("application/json; charset=\"utf-8\"")]
    public async Task A_body_with_utf_8_or_no_charset_is_read_as_before(string contentType)   // criterion 6
    {
        using var response = await SendAsync(
            "POST", "/auth/login", contentType, $$"""{"email":"{{Factory.SeedEmail}}","password":"{{Factory.SeedPassword}}"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("application/json; charset=utf-8", false)]
    [InlineData("application/json; charset=UTF-8", false)]
    [InlineData("application/json; charset=\"utf-8\"", false)]     // quoted: the parsed value keeps its quotes, the guard must not
    [InlineData("application/json; charset=\"UTF-8\"", false)]
    [InlineData("application/json; charset=\"utf-16\"", true)]
    [InlineData("application/json; charset=utf-16", true)]
    [InlineData("application/json; charset=\"\"", true)]            // an empty charset is not UTF-8 either
    [InlineData("application/json; charset=utf-8; foo=bar", false)]   // another parameter changes nothing
    [InlineData("application/json; charset=utf-8; charset=utf-16", true)]   // twice: the parser reports only one of them, so the guard looks at every one, and a body declared utf-16 anywhere is refused
    [InlineData("application/json; charset=utf-16; charset=utf-8", true)]
    [InlineData("application/json; charset=utf-8; charset=UTF-8", false)]
    [InlineData("application/json", false)]
    [InlineData("text/plain; charset=utf-16", false)]                // not JSON: the handler's own 400
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not a content type", false)]
    public void The_guard_reads_the_charset_without_its_quotes(string? contentType, bool refused)
    {
        Assert.Equal(refused, JsonCharsetGuard.IsRefused(contentType));
    }

    [Fact]
    public async Task A_body_that_is_not_json_at_all_keeps_its_own_answer()
    {
        using var response = await SendAsync("POST", "/auth/password/forgot", "text/plain; charset=utf-16");

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);   // the handler's 400, not a 415
    }

    [Fact]
    public async Task Refresh_and_logout_read_no_body_and_are_not_checked()
    {
        using var refresh = await SendAsync("POST", "/auth/refresh", Utf16);
        using var logout = await SendAsync("POST", "/auth/logout", Utf16);

        await SessionApi.AssertInvalidGrantAsync(refresh);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    [Fact]
    public async Task A_get_with_such_a_header_is_left_alone()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/health") { Content = new StringContent("", Encoding.UTF8) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(Utf16);

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/auth/refresh", false)]
    [InlineData("POST", "/auth/refresh/", false)]
    [InlineData("POST", "/AUTH/Logout", false)]
    [InlineData("POST", "/auth/logout/", false)]
    [InlineData("GET", "/auth/login", false)]
    [InlineData("HEAD", "/auth/health", false)]
    [InlineData("POST", "/api/notes", false)]
    [InlineData("POST", "/auth/login", true)]
    [InlineData("POST", "/AUTH/LOGIN/", true)]
    [InlineData("PUT", "/auth/org/roles/x", true)]
    [InlineData("PATCH", "/auth/org", true)]
    [InlineData("DELETE", "/auth/org/members/x", true)]
    public void The_guard_applies_to_the_four_methods_under_auth_except_refresh_and_logout(string method, string path, bool applies)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(applies, JsonCharsetGuard.Applies(context.Request));
    }
}
