namespace Auth.IntegrationTests.Infrastructure;

/// <summary>The headers of spec 0008 → Security headers, asserted the same way by every test that sees an answer.</summary>
public static class SecurityHeadersApi
{
    public const string StrictPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>The single value of a header, looked for among the response's headers and its content's.</summary>
    public static string HeaderValue(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);

        var found = response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values);
        Assert.True(found, $"The response has no {name} header.");
        return Assert.Single(values!);
    }

    /// <summary>
    /// Every header of the table, no <c>Server</c> and no <c>Strict-Transport-Security</c> (the proxy sends that one). An answer is
    /// <c>no-store</c> with <c>Pragma: no-cache</c>, except the key set and the OpenAPI document (<paramref name="cacheable"/>), which
    /// send no <c>Cache-Control</c> at all.
    /// </summary>
    public static void AssertSecurityHeaders(HttpResponseMessage response, bool cacheable = false)
    {
        ArgumentNullException.ThrowIfNull(response);

        Assert.Equal("nosniff", HeaderValue(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", HeaderValue(response, "X-Frame-Options"));
        Assert.Equal(StrictPolicy, HeaderValue(response, "Content-Security-Policy"));
        Assert.Equal("no-referrer", HeaderValue(response, "Referrer-Policy"));
        Assert.Equal("same-origin", HeaderValue(response, "Cross-Origin-Resource-Policy"));
        Assert.False(response.Headers.Contains("Server"), "No Server header is sent.");
        Assert.False(response.Headers.Contains("Strict-Transport-Security"), "TLS ends at the proxy, which sends HSTS.");
        if (cacheable)
        {
            Assert.False(response.Headers.Contains("Cache-Control"), "The key set and the OpenAPI document send no Cache-Control.");
            Assert.False(response.Headers.Contains("Pragma"));
        }
        else
        {
            Assert.Equal("no-store", HeaderValue(response, "Cache-Control"));
            Assert.Equal("no-cache", HeaderValue(response, "Pragma"));
        }
    }
}
