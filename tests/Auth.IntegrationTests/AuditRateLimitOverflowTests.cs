using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.IntegrationTests;

/// <summary>
/// Above the limiter's cap on windows (verification round 1, V3-F2) the partitions that come late share one counter, and the audit
/// log treats that counter as one source: a flood from many addresses is one row per policy and minute, which still names the full
/// address of the request that was refused.
/// </summary>
public sealed class AuditRateLimitOverflowTests : AuditTestBase
{
    public AuditRateLimitOverflowTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(RateLimitSettings.EnabledKey, "true").WithSetting(RateLimitSettings.PermitKey(RatePolicy.Login), "3");
        Factory.WithServices(services => services.Replace(ServiceDescriptor.Singleton(new SlidingWindowLimiter(Clock, 2))));   // a cap of two windows
    }

    private Task<HttpResponseMessage> UnknownLogin(int number, string remote) =>
        RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", remote, null, $$"""{"email":"nobody-{{number}}@example.test","password":"Wrong-Password-1"}""");

    [Fact]
    public async Task Refusals_above_the_cap_are_one_event_and_the_row_names_the_full_address_of_the_first()
    {
        foreach (var (number, remote) in new[] { (1, "203.0.113.1"), (2, "203.0.113.2") })   // two windows: the cap
        {
            using var answered = await UnknownLogin(number, remote);
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        foreach (var (number, remote) in new[] { (3, "203.0.113.3"), (4, "203.0.113.4"), (5, "203.0.113.5") })   // three requests, three addresses
        {
            using var answered = await UnknownLogin(number, remote);
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);   // the shared counter holds the limit of the policy
        }

        foreach (var (number, remote) in new[] { (6, "203.0.113.6"), (7, "203.0.113.7"), (8, "203.0.113.8") })
        {
            using var refused = await UnknownLogin(number, remote);
            await RateLimitApi.AssertTooManyRequestsAsync(refused);   // a fourth address finds the shared counter full
        }

        var row = await SingleAsync(AuditKinds.RateLimitHit);   // three refusals from three addresses: one row
        Assert.Equal("203.0.113.6", row.ClientIp);
        Assert.Equal("login", AuditApi.Text(row, "policy"));
        Assert.Equal("3", AuditApi.Text(row, "limit"));

        using var old = await UnknownLogin(9, "203.0.113.1");   // an address that has a window keeps its own counter
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
    }
}
