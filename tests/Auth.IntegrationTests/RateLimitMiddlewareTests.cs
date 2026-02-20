using System.Net;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.IntegrationTests;

public sealed class RateLimitMiddlewareTests : SessionTestBase
{
    private const string Remote = "203.0.113.9";

    private readonly CountingPasswordHasher _hasher = new();

    public RateLimitMiddlewareTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithRemoteAddressHeader()
            .WithoutHostedService<MailDispatchService>()   // nothing may take the queued rows away while a test counts them
            .WithSetting(RateLimitSettings.EnabledKey, "true")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Login), "3")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Refresh), "3")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Email), "3")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Invite), "3")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.General), "5")
            .WithServices(services => services.Replace(ServiceDescriptor.Singleton<IPasswordHasher<ApplicationUser>>(_hasher)));
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? remote = Remote, string? forwardedFor = null) =>
        RateLimitApi.SendAsync(Client, method, path, remote, forwardedFor);

    /// <summary>A login of an address that nobody has, a new one each time: the lockout of spec 0003 must not answer in its place.</summary>
    private Task<HttpResponseMessage> UnknownLogin(int number, string? remote = Remote, string path = "/auth/login") =>
        RateLimitApi.SendAsync(Client, HttpMethod.Post, path, remote, null, $$"""{"email":"nobody-{{number}}@example.test","password":"Wrong-Password-1"}""");

    [Fact]
    public async Task The_request_over_the_limit_is_a_429_that_evaluates_no_password_and_changes_no_streak()   // criterion 1
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await UnknownLogin(i);
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        Assert.Equal(3, _hasher.VerifiedHashes.Count);

        using var refused = await UnknownLogin(4);

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
        Assert.Equal(3, _hasher.VerifiedHashes.Count);   // no password was evaluated, not even against the decoy
        using var scope = Factory.Services.CreateScope();
        Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<AuthDbContext>().LoginStreaks.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_429_says_how_long_to_wait_on_the_clock()   // criterion 1
    {
        // The limiter counts from the moment it was made, on the timestamp of the injected clock, and the fake clock stands still
        // until a test moves it: the host was built before this method, so the first request is exactly on a segment boundary
        // and the arithmetic below does not depend on when the test runs.
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await UnknownLogin(i);
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        Clock.Advance(TimeSpan.FromSeconds(25));
        using var refused = await UnknownLogin(4);

        Assert.Equal(35, await RateLimitApi.AssertTooManyRequestsAsync(refused));
    }

    [Fact]
    public async Task A_request_over_the_limit_is_not_counted_and_the_window_slides()
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await UnknownLogin(i);
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        for (var i = 4; i <= 12; i++)
        {
            using var refused = await UnknownLogin(i);
            await RateLimitApi.AssertTooManyRequestsAsync(refused);
        }

        Clock.Advance(TimeSpan.FromSeconds(60));
        using var again = await UnknownLogin(13);

        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/auth/login", 3)]
    [InlineData("POST", "/auth/refresh", 3)]
    [InlineData("POST", "/auth/password/forgot", 3)]
    [InlineData("POST", "/auth/password/reset", 3)]
    [InlineData("POST", "/auth/email/verify/request", 3)]
    [InlineData("POST", "/auth/email/verify", 3)]
    [InlineData("POST", "/auth/invites/preview", 3)]
    [InlineData("POST", "/auth/invites/accept", 3)]
    [InlineData("GET", "/auth/health", 5)]
    [InlineData("GET", "/auth/.well-known/jwks.json", 5)]
    [InlineData("POST", "/auth/logout", 5)]
    [InlineData("GET", "/auth/me", 5)]
    public async Task Each_policy_refuses_at_its_own_number(string method, string path, int limit)   // criterion 1
    {
        var http = new HttpMethod(method);
        for (var i = 1; i <= limit; i++)
        {
            using var answered = await Send(http, path);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, answered.StatusCode);
        }

        using var refused = await Send(http, path);

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
    }

    [Fact]
    public async Task The_four_email_endpoints_share_one_limit_and_the_two_invitation_endpoints_another()
    {
        foreach (var path in new[] { "/auth/password/forgot", "/auth/password/reset", "/auth/email/verify/request" })
        {
            using var answered = await Send(HttpMethod.Post, path);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, answered.StatusCode);
        }

        using var fourth = await Send(HttpMethod.Post, "/auth/email/verify");
        await RateLimitApi.AssertTooManyRequestsAsync(fourth);

        foreach (var path in new[] { "/auth/invites/preview", "/auth/invites/accept", "/auth/invites/preview" })
        {
            using var answered = await Send(HttpMethod.Post, path);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, answered.StatusCode);   // another policy, its own count
        }
    }

    [Fact]
    public async Task A_request_refused_by_the_email_policy_queues_no_mail()
    {
        for (var i = 1; i <= 3; i++)
        {
            using var accepted = await RateLimitApi.SendAsync(
                Client, HttpMethod.Post, "/auth/password/forgot", Remote, null, $$"""{"email":"someone-{{i}}@example.test"}""");
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        Assert.Equal(3, await QueuedMailsAsync());   // a request queues one row, also for an address that has no account (spec 0004)

        using var refused = await RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/password/forgot", Remote, null, """{"email":"someone-4@example.test"}""");

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
        Assert.Equal(3, await QueuedMailsAsync());   // the refused request queued nothing
    }

    private async Task<int> QueuedMailsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().MailRequests.CountAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_path_is_compared_without_regard_to_case_or_a_trailing_slash()
    {
        foreach (var path in new[] { "/auth/login", "/AUTH/Login", "/auth/login/" })
        {
            using var answered = await UnknownLogin(1, Remote, path);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, answered.StatusCode);
        }

        using var refused = await UnknownLogin(2, Remote, "/auth/LOGIN/");

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
    }

    [Fact]
    public async Task A_request_counts_against_one_policy_only()
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await UnknownLogin(i);
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var refused = await UnknownLogin(4);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        using var refresh = await Send(HttpMethod.Post, "/auth/refresh");
        using var health = await Send(HttpMethod.Get, "/auth/health");
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Nothing_outside_auth_is_limited()
    {
        for (var i = 0; i < 20; i++)
        {
            using var answered = await Send(HttpMethod.Get, "/nope");
            Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);
        }
    }

    [Fact]
    public async Task Two_addresses_are_counted_apart()
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await UnknownLogin(i, "203.0.113.9");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var refused = await UnknownLogin(4, "203.0.113.9");
        using var other = await UnknownLogin(5, "203.0.113.10");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [Fact]
    public async Task An_ipv4_address_mapped_into_ipv6_is_the_same_address()
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await UnknownLogin(i, "203.0.113.9");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var mapped = await UnknownLogin(4, "::ffff:203.0.113.9");

        Assert.Equal(HttpStatusCode.TooManyRequests, mapped.StatusCode);
    }

    [Fact]
    public async Task An_ipv6_address_is_counted_with_the_rest_of_its_64_bit_network()
    {
        using var first = await UnknownLogin(1, "2001:db8:1:2::1");
        using var second = await UnknownLogin(2, "2001:db8:1:2::2");
        using var third = await UnknownLogin(3, "2001:db8:1:2:aaaa:bbbb:cccc:dddd");
        using var fourth = await UnknownLogin(4, "2001:db8:1:2::3");
        using var elsewhere = await UnknownLogin(5, "2001:db8:1:3::1");

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, elsewhere.StatusCode);
    }

    [Fact]
    public async Task Without_a_trusted_proxy_a_forwarded_for_header_changes_nothing()   // criterion 2
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await RateLimitApi.SendAsync(
                Client, HttpMethod.Post, "/auth/login", Remote, "198.51.100." + i, $$"""{"email":"nobody-{{i}}@example.test","password":"Wrong-Password-1"}""");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var refused = await RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", Remote, "198.51.100.99", """{"email":"nobody-4@example.test","password":"Wrong-Password-1"}""");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }
}
