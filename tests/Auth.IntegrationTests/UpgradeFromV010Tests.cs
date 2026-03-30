using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Auth.IntegrationTests;

/// <summary>Toviq runs v0.1.0: its database, made by the migrations up to <c>AddAuditEvents</c>, takes the new one and keeps what it holds.</summary>
public sealed class UpgradeFromV010Tests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string LastMigrationOfV010 = "20260221111842_AddAuditEvents";
    private const string StoredManifestOfV010 = """{"permissions":["a:b"],"default_roles":[{"name":"admin","permissions":["*"]}]}""";

    [Fact]
    public async Task A_database_of_v0_1_0_gets_the_table_keeps_its_rows_and_reads_its_manifest_as_invite_only()   // Review Focus 1
    {
        await using var factory = new AuthAppFactory(postgres, keys);   // never built: only its connection string is used
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(factory.ConnectionString).UseOpenIddict().Options;
        var cancellation = TestContext.Current.CancellationToken;

        await using (var old = new AuthDbContext(options))
        {
            await old.GetService<IMigrator>().MigrateAsync(LastMigrationOfV010, cancellation);
            Assert.Equal(0, await old.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_tables WHERE tablename = 'PendingSignups'""").SingleAsync(cancellation));
            await old.Database.ExecuteSqlAsync(
                $"""INSERT INTO "Companies" ("Id", "Name", "CreatedAt") VALUES ({Guid.NewGuid()}, 'Toviq', now())""", cancellation);
            await old.Database.ExecuteSqlAsync(
                $"""INSERT INTO "ActiveManifests" ("Id", "Content", "StoredAt") VALUES (1, {StoredManifestOfV010}, now())""", cancellation);
        }

        await using (var upgraded = new AuthDbContext(options))
        {
            await upgraded.Database.MigrateAsync(cancellation);

            Assert.Equal(0, await upgraded.PendingSignups.CountAsync(cancellation));
            Assert.Equal("Toviq", await upgraded.Companies.Select(c => c.Name).SingleAsync(cancellation));
            var stored = await upgraded.ActiveManifests.SingleAsync(cancellation);
            Assert.Equal(SignupMode.InviteOnly, Manifest.FromJson(stored.Content)!.Signup);
        }
    }
}
