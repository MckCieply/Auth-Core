using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Network;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task A_proxy_variable_of_the_machine_in_list_form_cannot_make_a_test_host_trust_a_proxy()
    {
        const string Variable = "Auth__Proxy__KnownNetworks__0";
        Environment.SetEnvironmentVariable(Variable, "10.250.0.0/24");
        try
        {
            await using var factory = new AuthAppFactory(Postgres, Keys);

            using var client = factory.CreateClient();

            Assert.True(factory.Services.GetRequiredService<ProxySettings>().IsEmpty);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }
    }
}
