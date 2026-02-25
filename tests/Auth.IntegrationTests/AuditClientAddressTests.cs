using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Network;

namespace Auth.IntegrationTests;

public sealed class AuditClientAddressTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private Task<HttpResponseMessage> WrongLogin(string remote, string? forwardedFor) =>
        RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", remote, forwardedFor, """{"email":"nobody@example.test","password":"Wrong-Password-1"}""");

    [Fact]
    public async Task With_no_trusted_proxy_the_recorded_address_is_the_connections_not_the_header()   // criterion 2
    {
        using var response = await WrongLogin("203.0.113.9", "198.51.100.77");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal("203.0.113.9", (await SingleAsync(AuditKinds.LoginFailed)).ClientIp);
    }

    [Fact]
    public async Task An_ipv4_address_mapped_into_ipv6_is_recorded_as_the_ipv4_address()
    {
        using var response = await WrongLogin("::ffff:203.0.113.9", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal("203.0.113.9", (await SingleAsync(AuditKinds.LoginFailed)).ClientIp);
    }

    [Fact]
    public async Task A_request_with_no_address_is_recorded_as_unknown()
    {
        using var response = await LoginApi.Login(Client, "nobody@example.test", "Wrong-Password-1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal("unknown", (await SingleAsync(AuditKinds.LoginFailed)).ClientIp);
    }
}

public sealed class AuditClientAddressBehindProxyTests : AuditTestBase
{
    public AuditClientAddressBehindProxyTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(ProxySettings.KnownNetworksKey + ":0", "10.250.0.0/24");
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_the_whole_client_address_is_recorded_and_what_the_client_wrote_is_not()   // criterion 2
    {
        using var response = await RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", "10.250.0.2", "9.9.9.9, 2001:db8:1:2::77",
            """{"email":"nobody@example.test","password":"Wrong-Password-1"}""");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal("2001:db8:1:2::77", (await SingleAsync(AuditKinds.LoginFailed)).ClientIp);   // the whole address, though the limiter counts the /64
    }
}
