using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.RateLimiting;

namespace Auth.IntegrationTests;

public sealed class SignupRateLimitTests : SignupTestBase
{
    private const string Remote = "203.0.113.9";

    public SignupRateLimitTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(RateLimitSettings.EnabledKey, "true")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Email), "3");
    }

    private Task<HttpResponseMessage> RequestFrom(string remote, int number) =>
        RateLimitApi.SendAsync(
            Client, HttpMethod.Post, SignupApi.RequestPath, remote, null, $$"""{"email":"a{{number}}@firma.test","company_name":"Firma"}""");

    [Fact]
    public async Task The_request_counts_against_the_mail_policy_of_the_per_ip_limit()   // criterion 18
    {
        for (var i = 1; i <= 3; i++)
        {
            using var accepted = await RequestFrom(Remote, i);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var refused = await RequestFrom(Remote, 4);
        await RateLimitApi.AssertTooManyRequestsAsync(refused);
        Assert.Equal(3, (await PendingAsync()).Count);   // the request over the limit did no other work

        using var otherAddress = await RequestFrom("203.0.113.10", 5);
        Assert.Equal(HttpStatusCode.Accepted, otherAddress.StatusCode);
    }
}
