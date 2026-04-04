using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class SignupCompleteDisabledTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static readonly string[] Bodies =
    [
        """{"token":"x","password":"Chosen-Passw0rd","company_name":"Firma"}""",
        "not json",
        "",
        "{}",
    ];

    [Theory]
    [MemberData(nameof(SignupVariants.Off), MemberType = typeof(SignupVariants))]
    public async Task Complete_off_answers_403_before_the_body_is_read_and_creates_nothing(string variant)   // criterion 2
    {
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), variant);
        using var client = factory.CreateClient();
        var (usersBefore, tokensBefore) = await CountsAsync(factory);

        foreach (var body in Bodies)
        {
            using var response = await AccountApi.PostRaw(client, SignupApi.CompletePath, body);
            await SignupApi.AssertDisabledAsync(response);
        }

        using var wrongType = await AccountApi.PostRaw(client, SignupApi.CompletePath, Bodies[0], "text/plain");
        await SignupApi.AssertDisabledAsync(wrongType);

        // The charset guard of spec 0008 comes before the 403 (Amendment 3).
        using var utf16 = new HttpRequestMessage(HttpMethod.Post, SignupApi.CompletePath) { Content = new StringContent(Bodies[0], Encoding.UTF8) };
        utf16.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=utf-16");
        using var unsupported = await client.SendAsync(utf16);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        Assert.Equal("""{"error":"unsupported_media_type"}""", await unsupported.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.Equal(1, await db.Companies.CountAsync(TestContext.Current.CancellationToken));   // the development company only
        Assert.Equal(0, await db.PendingSignups.CountAsync(TestContext.Current.CancellationToken));

        // No account, no session and no audit row of a sign-up or of a sign-in came out of any of the refused calls.
        Assert.Equal((usersBefore, tokensBefore), await CountsAsync(factory));
        Assert.Equal(0, await db.AuditEvents.CountAsync(
            a => a.Kind.StartsWith("signup.") || a.Kind == AuditKinds.LoginSucceeded, TestContext.Current.CancellationToken));
    }

    /// <summary>The accounts and the OpenIddict token entries of the host's database.</summary>
    private static async Task<(int Users, long Tokens)> CountsAsync(AuthAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var users = await db.Users.CountAsync(TestContext.Current.CancellationToken);
        var tokens = await db.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM "OpenIddictTokens" """)
            .SingleAsync(TestContext.Current.CancellationToken);
        return (users, tokens);
    }

    [Fact]
    public async Task The_per_ip_limit_comes_before_the_403_of_a_completion_that_is_off()   // Amendment 3
    {
        const string Remote = "203.0.113.9";
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithRemoteAddressHeader()
            .WithSetting(RateLimitSettings.EnabledKey, "true")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Invite), "1");
        using var client = factory.CreateClient();

        using var first = await RateLimitApi.SendAsync(client, HttpMethod.Post, SignupApi.CompletePath, Remote, null, Bodies[0]);
        await SignupApi.AssertDisabledAsync(first);

        // The second one is the limiter's 429, not the 403 again.
        using var second = await RateLimitApi.SendAsync(client, HttpMethod.Post, SignupApi.CompletePath, Remote, null, Bodies[0]);
        await RateLimitApi.AssertTooManyRequestsAsync(second);
    }

    [Fact]
    public async Task A_completion_after_sign_up_was_turned_off_is_403_and_consumes_nothing()   // spec 0009 → General rules
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        await using var on = new AuthAppFactory(postgres, keys, database).WithSignup();
        var token = await PendingSignupSeed.IssueAsync(on.Services, "anna@firma.test", "Firma");

        await using var off = new AuthAppFactory(postgres, keys, database);   // the same database, the manifest of v0.1.0 again
        using var client = off.CreateClient();
        using var response = await SignupApi.Complete(client, token, "Chosen-Passw0rd", "Firma");

        await SignupApi.AssertDisabledAsync(response);
        using var scope = off.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var pending = await db.PendingSignups.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EmailTokens.HashOf(token), pending.TokenHash);
        Assert.Equal(0, await db.Users.CountAsync(u => u.Email == "anna@firma.test", TestContext.Current.CancellationToken));
    }
}
