using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class SignupDisabledRequestTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static readonly string[] Bodies =
    [
        """{"email":"anna@firma.test","company_name":"Firma"}""",
        "not json",
        "",
        "{}",
    ];

    [Theory]
    [MemberData(nameof(SignupVariants.Off), MemberType = typeof(SignupVariants))]
    public async Task Sign_up_off_answers_403_before_the_body_is_read_and_creates_nothing(string variant)   // criteria 2, 17
    {
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), variant);
        using var client = factory.CreateClient();

        foreach (var body in Bodies)
        {
            using var response = await AccountApi.PostRaw(client, SignupApi.RequestPath, body);
            await SignupApi.AssertDisabledAsync(response);
        }

        using var wrongType = await AccountApi.PostRaw(client, SignupApi.RequestPath, Bodies[0], "text/plain");
        await SignupApi.AssertDisabledAsync(wrongType);

        // The charset guard of spec 0008 comes before the 403 (Amendment 3).
        using var utf16 = new HttpRequestMessage(HttpMethod.Post, SignupApi.RequestPath) { Content = new StringContent(Bodies[0], Encoding.UTF8) };
        utf16.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=utf-16");
        using var unsupported = await client.SendAsync(utf16);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        Assert.Equal("""{"error":"unsupported_media_type"}""", await unsupported.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.Equal(0, await db.PendingSignups.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.MailRequests.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.MailRequestLimits.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.AuditEvents.CountAsync(a => a.Kind == AuditKinds.SignupRequested, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_per_ip_limit_comes_before_the_403_of_a_sign_up_that_is_off()   // Amendment 3
    {
        const string Remote = "203.0.113.9";
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithRemoteAddressHeader()
            .WithSetting(RateLimitSettings.EnabledKey, "true")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Email), "1");
        using var client = factory.CreateClient();

        using var first = await RateLimitApi.SendAsync(client, HttpMethod.Post, SignupApi.RequestPath, Remote, null, Bodies[0]);
        await SignupApi.AssertDisabledAsync(first);

        using var second = await RateLimitApi.SendAsync(client, HttpMethod.Post, SignupApi.RequestPath, Remote, null, Bodies[0]);
        await RateLimitApi.AssertTooManyRequestsAsync(second);
    }
}
