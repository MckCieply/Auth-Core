using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Network;

namespace Auth.IntegrationTests;

public sealed class ClientAddressGuardTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public async Task A_host_started_with_the_framework_switch_for_forwarded_headers_does_not_start_and_names_it()
    {
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED=true makes the default host builder add the middleware with both lists cleared,
        // which believes every sender; it runs before the pipeline of Auth-Core, so only a refusal to start keeps it out.
        await using var factory = new AuthAppFactory(Postgres, Keys).WithSetting("ForwardedHeaders_Enabled", "true");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED", ex.ToString(), StringComparison.Ordinal);
        Assert.Contains(ProxySettings.KnownNetworksKey, ex.ToString(), StringComparison.Ordinal);
    }
}
