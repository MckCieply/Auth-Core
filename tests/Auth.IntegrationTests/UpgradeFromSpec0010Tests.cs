using System.Net;
using System.Text.Json;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, criterion 30: an instance of spec 0010 upgrades without noticing.</summary>
public sealed class UpgradeFromSpec0010Tests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string LastMigrationOfSlice0010 = "20260410163512_AddSpaceKinds";
    private const string StoredBySlice0010 = """{"permissions":["docs:read"],"default_roles":[{"name":"admin","permissions":["*"]}],"signup":"self-service","private":{"signup":"self-service","permissions":["docs:read"]}}""";
    private static readonly string[] LoginClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"];

    [Fact]
    public async Task A_database_of_slice_0010_takes_the_ticket_table_and_keeps_what_it_holds()   // criterion 30
    {
        await using var factory = new AuthAppFactory(postgres, keys);   // never built: only its connection string is used
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(factory.ConnectionString).UseOpenIddict().Options;
        var cancellation = TestContext.Current.CancellationToken;
        var company = Guid.NewGuid();

        await using (var old = new AuthDbContext(options))
        {
            await old.GetService<IMigrator>().MigrateAsync(LastMigrationOfSlice0010, cancellation);
            Assert.False(await old.Database.SqlQuery<bool>($"""SELECT to_regclass('"ProviderTickets"') IS NOT NULL AS "Value" """).SingleAsync(cancellation));
            await old.Database.ExecuteSqlAsync(
                $"""INSERT INTO "Companies" ("Id", "Name", "Kind", "CreatedAt") VALUES ({company}, 'Toviq', 1, now())""", cancellation);
            await old.Database.ExecuteSqlAsync(
                $"""INSERT INTO "ActiveManifests" ("Id", "Content", "StoredAt") VALUES ({ActiveManifest.TheOnlyId}, {StoredBySlice0010}, now())""", cancellation);
        }

        await using (var upgraded = new AuthDbContext(options))
        {
            await upgraded.Database.MigrateAsync(cancellation);

            Assert.Equal("Toviq", await upgraded.Companies.Where(c => c.Id == company).Select(c => c.Name).SingleAsync(cancellation));
            Assert.Equal(0, await upgraded.ProviderTickets.CountAsync(cancellation));
            Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync(cancellation));

            // The manifest stored by spec 0010 is still there byte for byte and reads as a manifest with no provider.
            var stored = await upgraded.ActiveManifests.AsNoTracking().SingleAsync(cancellation);
            Assert.Equal(StoredBySlice0010, stored.Content);
            var manifest = Manifest.FromJson(stored.Content);
            Assert.NotNull(manifest);
            Assert.Empty(manifest.Providers);
            Assert.True(manifest.PrivateSignup);
        }
    }

    [Fact]
    public async Task An_instance_with_the_settings_of_spec_0010_starts_healthy_offers_no_provider_and_issues_the_same_tokens()   // criterion 30
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup();
        using var client = factory.CreateClient();

        using (var health = await client.GetAsync("/auth/health", TestContext.Current.CancellationToken))
        {
            Assert.Equal("Healthy", await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var options = await client.GetAsync("/auth/options", TestContext.Current.CancellationToken))
        {
            Assert.Equal("""{"signup":"self-service","private_signup":"self-service","providers":[]}""", await options.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.Empty(factory.Services.GetRequiredService<ProviderCatalog>().Offered);
        var token = await LoginApi.LoginToken(client, factory);
        Assert.Equal(LoginClaims, AccessTokens.Payload(token).Select(p => p.Key).Order(StringComparer.Ordinal));
        using var me = await TenancyApi.Get(client, "/auth/me", token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(body.RootElement.GetProperty("has_password").GetBoolean());
        Assert.Equal(0, body.RootElement.GetProperty("linked_providers").GetArrayLength());
    }
}
