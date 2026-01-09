using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public class SkeletonTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    [Fact]
    public async Task Health_endpoint_returns_200()
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/auth/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
