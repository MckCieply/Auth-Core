using System.Collections.Concurrent;
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Seeding;
using Auth.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Auth.IntegrationTests;

public sealed class AuditRefreshReuseTests : AuditTestBase
{
    private readonly OldRefreshTokens _oldTokens;

    // What a test-only handler, at the order of the reuse audit handler, saw: whether the validated token's principal carried the company claim.
    private readonly ConcurrentQueue<bool> _principalCarriedCompany = new();

    public AuditRefreshReuseTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        _oldTokens = OldRefreshTokens.Install(Factory);
        Factory.WithServices(services => services.Configure<OpenIddictServerOptions>(options => options.Handlers.Add(
            OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ValidateTokenContext>()
                .UseInlineHandler(context =>
                {
                    if (!string.IsNullOrEmpty(context.TokenId))
                    {
                        _principalCarriedCompany.Enqueue(context.Principal?.GetClaim(SessionPolicy.CompanyClaim) is not null);
                    }

                    return ValueTask.CompletedTask;
                })
                .SetOrder(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor.Order - 1)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build())));
    }

    private Task<int> SetLastUsedAtAsync(Guid userId, Guid companyId, DateTimeOffset? at) =>
        InDbAsync(db => db.Memberships.Where(m => m.UserId == userId && m.CompanyId == companyId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedAt, at), TestContext.Current.CancellationToken));

    [Fact]
    public async Task The_principal_of_the_reused_token_is_known_where_the_reuse_is_recorded()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        await SessionApi.RefreshOk(Client, login.RefreshToken);
        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(replay);

        Assert.Equal(2, _principalCarriedCompany.Count);   // the honest refresh and the replay
        Assert.All(_principalCarriedCompany, carried => Assert.True(carried, "context.Principal must carry session_org_id at the handler's order."));
    }

    [Fact]
    public async Task A_reuse_names_the_session_company_of_the_reused_token()
    {
        var user = await Factory.SeedUserIdAsync();
        var a = await DevCompanyIdAsync();
        var b = await CreateCompanyAsync("Second");
        await AddMembershipAsync(user, b, "user");
        var login = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal(a.ToString(), AccessTokens.Text(login.AccessToken, "org_id"));
        await SessionApi.RefreshOk(Client, login.RefreshToken);
        await SetLastUsedAtAsync(user, b, StorableTime.Now(Clock).AddHours(1));   // the sign-in order now says B

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(replay);

        var row = await SingleAsync(AuditKinds.RefreshReuseDetected);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal(a, row.OrgId);
        Assert.Equal(DevUserSeeder.DefaultOrgName, row.OrgName);
    }

    [Fact]
    public async Task A_reuse_of_a_token_without_the_session_company_names_the_sign_in_space()
    {
        var user = await Factory.SeedUserIdAsync();
        var a = await DevCompanyIdAsync();
        var b = await CreateCompanyAsync("Second");
        await AddMembershipAsync(user, b, "user");
        var old = await _oldTokens.LoginAsync(Client, Factory.SeedEmail, Factory.SeedPassword);
        var stored = await StoredTokens.RefreshClaimsAsync(Factory, old.RefreshToken);
        Assert.False(stored.ContainsKey("session_org_id"), "The minted token must be one of the code before spec 0015.");
        Assert.Equal(a.ToString(), AccessTokens.Text(old.AccessToken, "org_id"));
        await SessionApi.RefreshOk(Client, old.RefreshToken);   // consumes it
        await SetLastUsedAtAsync(user, b, StorableTime.Now(Clock).AddHours(1));   // the sign-in order now says B

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, old.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(replay);

        var row = await SingleAsync(AuditKinds.RefreshReuseDetected);
        Assert.Equal(b, row.OrgId);
        Assert.Equal("Second", row.OrgName);
    }

    [Fact]
    public async Task A_reuse_of_a_token_whose_session_company_is_gone_names_the_company_without_a_name()
    {
        var user = await Factory.SeedUserIdAsync();
        var c = await CreateCompanyAsync("Doomed");
        await AddMembershipAsync(user, c, "user");
        await SetLastUsedAtAsync(user, c, StorableTime.Now(Clock).AddHours(1));   // the sign-in lands in C
        var login = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal(c.ToString(), AccessTokens.Text(login.AccessToken, "org_id"));
        await SessionApi.RefreshOk(Client, login.RefreshToken);
        await InDbAsync(async db =>
        {
            await db.Memberships.Where(m => m.CompanyId == c).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            return await db.Companies.Where(x => x.Id == c).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        });

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(replay);

        var row = await SingleAsync(AuditKinds.RefreshReuseDetected);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal(c, row.OrgId);
        Assert.Null(row.OrgName);
    }

    [Fact]
    public async Task A_consumed_token_presented_after_the_leeway_is_recorded_once_with_its_account()   // criterion 8
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        var rotated = await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(replay);

        var row = await SingleAsync(AuditKinds.RefreshReuseDetected);
        var seed = await Factory.SeedUserIdAsync();
        Assert.Null(row.ActorUserId);   // whoever replayed the token is not known
        Assert.Equal(seed, row.SubjectUserId);
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal(DevUserSeeder.DefaultOrgName, row.OrgName);
        Assert.Equal("unknown", row.ClientIp);   // the helper sends no address

        using var again = await SessionApi.Refresh(Client, login.RefreshToken);   // the family is revoked now: nothing more to detect
        await SessionApi.AssertInvalidGrantAsync(again);
        Assert.Single(await AuditAsync(AuditKinds.RefreshReuseDetected));
        await AssertNoSecretsAsync(login.RefreshToken, login.AccessToken, rotated.RefreshToken, rotated.AccessToken);   // every token of the flow
    }

    [Fact]
    public async Task A_consumed_token_presented_inside_the_leeway_is_an_honest_retry_and_writes_nothing()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(TimeSpan.FromSeconds(10));
        _ = await SessionApi.RefreshOk(Client, login.RefreshToken);

        Assert.Empty(await AuditAsync(AuditKinds.RefreshReuseDetected));
    }

    [Fact]
    public async Task Tokens_that_are_unknown_or_revoked_or_current_write_nothing()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        using (var unknown = await SessionApi.Refresh(Client, "a-cookie-nobody-issued"))
        {
            await SessionApi.AssertInvalidGrantAsync(unknown);
        }

        var current = await SessionApi.RefreshOk(Client, login.RefreshToken);
        _ = await SessionApi.RefreshOk(Client, current.RefreshToken);
        using (var logout = await SessionApi.Logout(Client, current.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        using (var revoked = await SessionApi.Refresh(Client, current.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(revoked);
        }

        Assert.Empty(await AuditAsync(AuditKinds.RefreshReuseDetected));
    }

    [Fact]
    public async Task A_replay_exactly_at_the_leeway_is_rejected_and_recorded()   // the boundary is OpenIddict's, and ours is the same
    {
        var (rejected, rows) = await ReplayAfterAsync(SessionPolicy.ReuseLeeway);

        Assert.True(rejected);
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task A_replay_a_millisecond_inside_the_leeway_is_let_through_and_not_recorded()
    {
        var (rejected, rows) = await ReplayAfterAsync(SessionPolicy.ReuseLeeway - TimeSpan.FromMilliseconds(1));

        Assert.False(rejected);
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task A_row_exists_exactly_when_the_replay_is_answered_invalid_grant()
    {
        foreach (var delay in new[] { TimeSpan.FromMilliseconds(14_999), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(15_001) })
        {
            var (rejected, rows) = await ReplayAfterAsync(delay);
            Assert.Equal(rejected ? 1 : 0, rows);
            await InDbAsync(db => db.AuditEvents.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        }
    }

    private async Task<(bool Rejected, int Rows)> ReplayAfterAsync(TimeSpan delay)
    {
        // The clock starts at the real time, with sub-microsecond ticks, and PostgreSQL stores the redemption date to the microsecond:
        // from a whole second on, "exactly the leeway" is exact.
        Clock.SetUtcNow(new DateTimeOffset((Clock.GetUtcNow().UtcTicks / TimeSpan.TicksPerSecond + 1) * TimeSpan.TicksPerSecond, TimeSpan.Zero));
        var login = await SessionApi.LoginAsync(Client, Factory);
        await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(delay);
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        var rejected = replay.StatusCode == HttpStatusCode.Unauthorized;
        if (rejected)
        {
            await SessionApi.AssertInvalidGrantAsync(replay);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        return (rejected, (await AuditAsync(AuditKinds.RefreshReuseDetected)).Count);
    }

    // The lookup of a token that names its company reads Companies; one of before spec 0015 reads the sign-in space (Memberships).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failing_lookup_of_the_company_does_not_change_the_answer_or_stop_the_revocation(bool oldToken)
    {
        var login = oldToken
            ? await _oldTokens.LoginAsync(Client, Factory.SeedEmail, Factory.SeedPassword)
            : await SessionApi.LoginAsync(Client, Factory);
        var next = await SessionApi.RefreshOk(Client, login.RefreshToken);
        Clock.Advance(TimeSpan.FromSeconds(20));
        var rename = oldToken
            ? """ALTER TABLE "Memberships" RENAME TO "Memberships_away" """
            : """ALTER TABLE "Companies" RENAME TO "Companies_away" """;
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(rename, TestContext.Current.CancellationToken));

        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        using var afterReplay = await SessionApi.Refresh(Client, next.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(replay);        // not a 500: OpenIddict still decides the reuse
        await SessionApi.AssertInvalidGrantAsync(afterReplay);   // and still revoked the family
        Assert.Empty(await AuditAsync(AuditKinds.RefreshReuseDetected));   // nothing was written for it
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Warning && e.Category.EndsWith("RefreshReuseAuditHandler", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_handler_does_not_change_what_the_refresh_answers()   // spec 0002 stays as it is
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        var next = await SessionApi.RefreshOk(Client, login.RefreshToken);
        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        using var afterReplay = await SessionApi.Refresh(Client, next.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(replay);
        await SessionApi.AssertInvalidGrantAsync(afterReplay);   // the family died with the replay, as before
    }
}
