using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class HealthMethodsTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public async Task Get_answers_200_and_head_answers_200()   // criterion 5
    {
        using var get = await Client.GetAsync("/auth/health");
        using var head = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/auth/health"));

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("Healthy", await get.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        // No assertion on the body of the HEAD answer: the in-memory test server hands the health check's body ("Healthy") back on a
        // HEAD request, which Kestrel never sends. `scripts/e2e-hardening.sh` step 1 asserts the empty body against Kestrel.
        SecurityHeadersApi.AssertSecurityHeaders(head);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("OPTIONS")]
    public async Task Any_other_method_is_405_with_the_two_that_are_allowed(string method)   // criterion 5
    {
        using var response = await Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/auth/health"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("GET, HEAD", string.Join(", ", response.Content.Headers.Allow));
        SecurityHeadersApi.AssertSecurityHeaders(response);
    }
}
