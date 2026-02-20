using Auth.Server.Sessions;
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Auth.IntegrationTests;

/// <summary>The error branch of the handler that writes the refresh answer, driven directly: OpenIddict hardly ever reports <c>server_error</c>.</summary>
public sealed class SessionResponseHandlerTests
{
    private sealed record Written(int Status, string Body, string RetryAfter, string SetCookie, bool Handled);

    private static async Task<Written> RunAsync(string path, string error)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();

        // OpenIddict keeps the request of its transaction behind a weak reference.
        var transaction = new OpenIddictServerTransaction();
        transaction.Properties[typeof(HttpRequest).FullName!] = new WeakReference<HttpRequest>(http.Request);
        var context = new OpenIddictServerEvents.ApplyTokenResponseContext(transaction) { Response = new OpenIddictResponse { Error = error } };

        await new SessionResponseHandler().HandleAsync(context);

        http.Response.Body.Position = 0;
        return new Written(
            http.Response.StatusCode,
            await new StreamReader(http.Response.Body).ReadToEndAsync(),
            http.Response.Headers.RetryAfter.ToString(),
            http.Response.Headers.SetCookie.ToString(),
            context.IsRequestHandled);
    }

    [Fact]
    public async Task A_server_error_on_refresh_is_a_503_temporarily_unavailable_with_retry_after_and_no_cookie()   // criterion 3
    {
        var written = await RunAsync("/auth/refresh", "server_error");

        Assert.Equal(503, written.Status);
        Assert.Equal("""{"error":"temporarily_unavailable"}""", written.Body);
        Assert.Equal("5", written.RetryAfter);
        Assert.Equal("", written.SetCookie);
        Assert.True(written.Handled);
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("access_denied")]
    [InlineData("unsupported_grant_type")]
    public async Task Every_other_refresh_error_stays_the_uniform_401_invalid_grant(string error)
    {
        var written = await RunAsync("/auth/refresh", error);

        Assert.Equal(401, written.Status);
        Assert.Equal("""{"error":"invalid_grant"}""", written.Body);
        Assert.Equal("", written.RetryAfter);
    }

    [Fact]
    public async Task A_malformed_refresh_request_is_left_to_openiddict_as_a_400()
    {
        var written = await RunAsync("/auth/refresh", "invalid_request");

        Assert.Equal(200, written.Status);   // untouched here: OpenIddict's own writer makes the 400
        Assert.Equal("", written.Body);
        Assert.False(written.Handled);
    }

    [Fact]
    public async Task A_server_error_on_login_is_left_alone()
    {
        var written = await RunAsync("/auth/login", "server_error");

        Assert.Equal(200, written.Status);
        Assert.Equal("", written.Body);
        Assert.False(written.Handled);
    }
}
