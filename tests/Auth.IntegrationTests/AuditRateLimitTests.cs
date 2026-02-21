using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class AuditRateLimitTests : AuditTestBase
{
    public AuditRateLimitTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(RateLimitSettings.EnabledKey, "true").WithSetting(RateLimitSettings.PermitKey(RatePolicy.Login), "3");
    }

    private Task<HttpResponseMessage> UnknownLogin(int number, string remote) =>
        RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", remote, null, $$"""{"email":"nobody-{{number}}@example.test","password":"Wrong-Password-1"}""");

    private async Task ExhaustAsync(string remote, int first)
    {
        for (var i = 0; i < 3; i++)
        {
            using var answered = await UnknownLogin(first + i, remote);
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }
    }

    [Fact]
    public async Task A_refusal_is_recorded_once_per_address_and_policy_per_minute()   // criterion 8
    {
        await ExhaustAsync("203.0.113.9", 1);

        for (var i = 4; i <= 9; i++)
        {
            using var refused = await UnknownLogin(i, "203.0.113.9");
            await RateLimitApi.AssertTooManyRequestsAsync(refused);
        }

        var row = await SingleAsync(AuditKinds.RateLimitHit);   // six refusals, one row
        Assert.Equal("203.0.113.9", row.ClientIp);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.SubjectUserId);
        Assert.Equal("login", AuditApi.Text(row, "policy"));
        Assert.Equal("3", AuditApi.Text(row, "limit"));

        Clock.Advance(TimeSpan.FromSeconds(61));   // a minute later the window is empty and the next flood is its own event
        await ExhaustAsync("203.0.113.9", 20);
        using var again = await UnknownLogin(30, "203.0.113.9");
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);

        Assert.Equal(2, (await AuditAsync(AuditKinds.RateLimitHit)).Count);
    }

    [Fact]
    public async Task Another_address_and_another_policy_are_events_of_their_own()
    {
        await ExhaustAsync("203.0.113.9", 1);
        using (var first = await UnknownLogin(4, "203.0.113.9"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        }

        await ExhaustAsync("203.0.113.10", 10);
        using (var second = await UnknownLogin(14, "203.0.113.10"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        }

        var rows = await AuditAsync(AuditKinds.RateLimitHit);
        Assert.Equal(["203.0.113.10", "203.0.113.9"], rows.Select(r => r.ClientIp).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_refused_request_writes_no_login_row_for_it()   // nothing downstream ran
    {
        await ExhaustAsync("203.0.113.9", 1);
        var before = (await AuditAsync(AuditKinds.LoginFailed)).Count;

        using var refused = await UnknownLogin(4, "203.0.113.9");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(before, (await AuditAsync(AuditKinds.LoginFailed)).Count);
    }

    [Fact]
    public async Task A_failing_audit_write_never_fails_the_request()   // spec 0008: the write of a rate-limit hit never fails the request
    {
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));
        await ExhaustAsync("203.0.113.9", 1);   // the login rows fail to write too, and the logins still answer

        using var refused = await UnknownLogin(4, "203.0.113.9");

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
    }
}
