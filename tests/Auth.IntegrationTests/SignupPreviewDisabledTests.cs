using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class SignupPreviewDisabledTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static readonly string[] Bodies = ["""{"token":"x"}""", "not json", "", "{}"];

    [Theory]
    [MemberData(nameof(SignupVariants.Off), MemberType = typeof(SignupVariants))]
    public async Task Preview_off_answers_403_before_the_body_is_read(string variant)   // criterion 2
    {
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), variant);
        using var client = factory.CreateClient();

        foreach (var body in Bodies)
        {
            using var response = await AccountApi.PostRaw(client, SignupApi.PreviewPath, body);
            await SignupApi.AssertDisabledAsync(response);
        }

        using var wrongType = await AccountApi.PostRaw(client, SignupApi.PreviewPath, Bodies[0], "text/plain");
        await SignupApi.AssertDisabledAsync(wrongType);

        // The charset guard of spec 0008 comes before the 403 (Amendment 3).
        using var utf16 = new HttpRequestMessage(HttpMethod.Post, SignupApi.PreviewPath) { Content = new StringContent(Bodies[0], Encoding.UTF8) };
        utf16.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=utf-16");
        using var unsupported = await client.SendAsync(utf16);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        Assert.Equal("""{"error":"unsupported_media_type"}""", await unsupported.Content.ReadAsStringAsync());

        // Nothing was stored, queued, counted or written down: the 403 comes before the body is read, and no sign-up is made of it.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.Equal(0, await db.PendingSignups.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.MailRequests.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.MailRequestLimits.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.AuditEvents.CountAsync(a => a.Kind.StartsWith("signup."), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_per_ip_limit_comes_before_the_403_of_a_preview_that_is_off()   // Amendment 3
    {
        const string Remote = "203.0.113.9";
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithRemoteAddressHeader()
            .WithSetting(RateLimitSettings.EnabledKey, "true")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Invite), "1");
        using var client = factory.CreateClient();

        using var first = await RateLimitApi.SendAsync(client, HttpMethod.Post, SignupApi.PreviewPath, Remote, null, Bodies[0]);
        await SignupApi.AssertDisabledAsync(first);

        using var second = await RateLimitApi.SendAsync(client, HttpMethod.Post, SignupApi.PreviewPath, Remote, null, Bodies[0]);
        await RateLimitApi.AssertTooManyRequestsAsync(second);
    }

    [Fact]
    public async Task A_preview_after_sign_up_was_turned_off_is_403_and_changes_nothing()   // spec 0009 → General rules
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        await using var on = new AuthAppFactory(postgres, keys, database).WithSignup();
        var token = await PendingSignupSeed.IssueAsync(on.Services, "anna@firma.test", "Firma");

        await using var off = new AuthAppFactory(postgres, keys, database);   // the same database, the manifest of v0.1.0 again
        using var client = off.CreateClient();
        using var response = await SignupApi.Preview(client, token);

        await SignupApi.AssertDisabledAsync(response);
        using var scope = off.Services.CreateScope();
        var pending = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().PendingSignups.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EmailTokens.HashOf(token), pending.TokenHash);
    }
}
