using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.RateLimiting;

namespace Auth.IntegrationTests;

public sealed class SecurityHeadersOnRefusalTests : SessionTestBase
{
    public SecurityHeadersOnRefusalTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(RateLimitSettings.EnabledKey, "true").WithSetting(RateLimitSettings.PermitKey(RatePolicy.Login), "1");
    }

    [Fact]
    public async Task The_429_of_the_limiter_has_the_headers()   // criterion 4
    {
        using var first = await LoginApi.Login(Client, "nobody-1@example.test", "Wrong-Password-1");
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);

        using var refused = await LoginApi.Login(Client, "nobody-2@example.test", "Wrong-Password-1");

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
        SecurityHeadersApi.AssertSecurityHeaders(refused);
    }
}
