using System.Net;
using System.Text.Json;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012, criterion 27: an instance of spec 0011 upgrades without noticing.</summary>
public sealed class UpgradeFromSpec0011Tests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string LastMigrationOfSlice0011 = "20260424164100_AddProviderTickets";
    private static readonly string[] LoginClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"];

    [Fact]
    public async Task A_database_of_slice_0011_takes_the_session_table_and_the_provider_column_and_keeps_what_it_holds()   // criterion 27
    {
        await using var factory = new AuthAppFactory(postgres, keys);   // never built: only its connection string is used
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(factory.ConnectionString).UseOpenIddict().Options;
        var cancellation = TestContext.Current.CancellationToken;

        await using (var old = new AuthDbContext(options))
        {
            await old.GetService<IMigrator>().MigrateAsync(LastMigrationOfSlice0011, cancellation);
            Assert.False(await old.Database.SqlQuery<bool>($"""SELECT to_regclass('"SessionStates"') IS NOT NULL AS "Value" """).SingleAsync(cancellation));
            await old.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "MailRequests" ("Kind", "NormalizedEmail", "RequestedAt", "Attempts", "NextAttemptAt")
                VALUES (1, 'OLA@EXAMPLE.TEST', now(), 0, now())
                """,
                cancellation);
        }

        await using (var upgraded = new AuthDbContext(options))
        {
            await upgraded.Database.MigrateAsync(cancellation);

            Assert.Equal(0, await upgraded.SessionStates.CountAsync(cancellation));
            var row = await upgraded.MailRequests.AsNoTracking().SingleAsync(cancellation);
            Assert.Equal((MailKind.PasswordReset, "OLA@EXAMPLE.TEST", (string?)null), (row.Kind, row.NormalizedEmail, row.Provider));
            Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync(cancellation));
        }
    }

    [Fact]
    public async Task An_instance_with_the_settings_of_spec_0011_starts_healthy_offers_its_providers_and_issues_the_same_tokens()   // criterion 27
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup().WithProviders();
        using var client = SessionApi.CreateClient(factory);

        using (var health = await client.GetAsync("/auth/health", TestContext.Current.CancellationToken))
        {
            Assert.Equal("Healthy", await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var options = await client.GetAsync("/auth/options", TestContext.Current.CancellationToken))
        {
            Assert.Equal(
                """{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft"]}""",
                await options.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var session = await SessionApi.LoginAsync(client, factory);
        Assert.Equal(LoginClaims, AccessTokens.Payload(session.AccessToken).Select(p => p.Key).Order(StringComparer.Ordinal));
        using var me = await SettingsApi.Me(client, session);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.String, body.RootElement.GetProperty("confirmed_until").ValueKind);
    }

    [Fact]
    public async Task A_session_from_before_this_version_refreshes_as_before_and_its_window_is_closed()   // criterion 27, spec 0012 → Sessions
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        using var client = SessionApi.CreateClient(factory);
        var session = await SessionApi.LoginAsync(client, factory);
        using (var scope = factory.Services.CreateScope())
        {
            // What a session of spec 0011 is: an authorization with no row of values.
            await scope.ServiceProvider.GetRequiredService<AuthDbContext>().SessionStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var refreshed = await SessionApi.RefreshOk(client, session.RefreshToken);

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(client, refreshed));
        using var scope2 = factory.Services.CreateScope();
        Assert.Equal(0, await scope2.ServiceProvider.GetRequiredService<AuthDbContext>().SessionStates.CountAsync(TestContext.Current.CancellationToken));
    }
}
