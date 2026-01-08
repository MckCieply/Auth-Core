using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public class SkeletonTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Health_endpoint_returns_200()
    {
        await using var factory = new AuthAppFactory(postgres);
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/auth/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
