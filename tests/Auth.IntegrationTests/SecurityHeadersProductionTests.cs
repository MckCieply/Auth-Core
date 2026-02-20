using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class SecurityHeadersProductionTests : SessionTestBase
{
    public SecurityHeadersProductionTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithEnvironment("Production");
    }

    [Fact]
    public async Task Where_the_interactive_reference_is_not_served_its_path_gets_the_strict_policy()
    {
        using var response = await Client.GetAsync("/auth/scalar/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        SecurityHeadersApi.AssertSecurityHeaders(response);
    }
}
