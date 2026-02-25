using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.RateLimiting;

namespace Auth.IntegrationTests;

public sealed class RateLimitDefaultsTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public async Task The_test_host_has_the_limiter_off_so_that_no_other_test_meets_it()
    {
        for (var i = 1; i <= 40; i++)
        {
            using var answered = await LoginApi.Login(Client, $"nobody-{i}@example.test", "Wrong-Password-1");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }
    }

    [Fact]
    public async Task A_host_with_a_permit_that_is_not_a_number_does_not_start_and_names_the_key()
    {
        await using var factory = new AuthAppFactory(Postgres, Keys).WithSetting(RateLimitSettings.PermitKey(RatePolicy.Login), "many");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(RateLimitSettings.PermitKey(RatePolicy.Login), ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_with_a_proxy_that_is_not_an_address_does_not_start_and_names_the_key()
    {
        await using var factory = new AuthAppFactory(Postgres, Keys).WithSetting("Auth:Proxy:KnownProxies:0", "proxy.internal");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Auth:Proxy:KnownProxies", ex.ToString(), StringComparison.Ordinal);
    }
}
