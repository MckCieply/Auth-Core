using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Network;
using Auth.Server.RateLimiting;

namespace Auth.IntegrationTests;

public sealed class RateLimitProxyTests : SessionTestBase
{
    private const string Proxy = "10.250.0.2";

    public RateLimitProxyTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithRemoteAddressHeader()
            .WithSetting(RateLimitSettings.EnabledKey, "true")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Login), "3")
            .WithSetting(ProxySettings.KnownNetworksKey + ":0", "10.250.0.0/24");
    }

    private Task<HttpResponseMessage> LoginBehindProxy(int number, string forwardedFor, string remote = Proxy) =>
        RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", remote, forwardedFor, $$"""{"email":"nobody-{{number}}@example.test","password":"Wrong-Password-1"}""");

    [Fact]
    public async Task Two_clients_behind_the_trusted_proxy_are_counted_separately()   // criterion 2
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await LoginBehindProxy(i, "198.51.100.1");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var firstClient = await LoginBehindProxy(4, "198.51.100.1");
        using var secondClient = await LoginBehindProxy(5, "198.51.100.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, firstClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, secondClient.StatusCode);
    }

    [Fact]
    public async Task What_the_client_wrote_into_the_header_does_not_choose_its_partition()
    {
        // The proxy appends the address it saw: whatever comes before it was written by the client.
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await LoginBehindProxy(i, $"10.0.0.{i}, 198.51.100.1");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var refused = await LoginBehindProxy(4, "10.9.9.9, 198.51.100.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task From_an_address_that_is_not_the_proxy_the_header_is_ignored()   // criterion 2
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await LoginBehindProxy(i, $"198.51.100.{i}", remote: "203.0.113.9");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var refused = await LoginBehindProxy(4, "198.51.100.77", remote: "203.0.113.9");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }
}
