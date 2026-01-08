using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public class SkeletonTests
{
    [Fact]
    public async Task Health_endpoint_returns_200()
    {
        using var client = new AuthAppFactory().CreateClient();
        var response = await client.GetAsync("/auth/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
