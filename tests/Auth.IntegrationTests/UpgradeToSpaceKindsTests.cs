using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Auth.IntegrationTests;

/// <summary>A database of slice 0009 takes the kinds and keeps what it holds (spec 0010 → Spaces, Pending sign-ups).</summary>
public sealed class UpgradeToSpaceKindsTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string LastMigrationOfSlice0009 = "20260330193512_AddPendingSignups";

    [Fact]
    public async Task Rows_made_before_the_kinds_existed_are_an_organization_and_an_organization_sign_up()
    {
        await using var factory = new AuthAppFactory(postgres, keys);   // never built: only its connection string is used
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(factory.ConnectionString).UseOpenIddict().Options;
        var cancellation = TestContext.Current.CancellationToken;
        var company = Guid.NewGuid();

        await using (var old = new AuthDbContext(options))
        {
            await old.GetService<IMigrator>().MigrateAsync(LastMigrationOfSlice0009, cancellation);
            await old.Database.ExecuteSqlAsync(
                $"""INSERT INTO "Companies" ("Id", "Name", "CreatedAt") VALUES ({company}, 'Toviq', now())""", cancellation);
            await old.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "PendingSignups" ("NormalizedEmail", "Email", "CompanyName", "RequestedAt", "ExpiresAt")
                VALUES ('ANNA@FIRMA.TEST', 'anna@firma.test', 'Firma', now(), now() + interval '1 day')
                """,
                cancellation);
        }

        await using (var upgraded = new AuthDbContext(options))
        {
            await upgraded.Database.MigrateAsync(cancellation);

            Assert.Equal(CompanyKind.Organization, await upgraded.Companies.Where(c => c.Id == company).Select(c => c.Kind).SingleAsync(cancellation));
            var pending = await upgraded.PendingSignups.AsNoTracking().SingleAsync(cancellation);
            Assert.Equal(SignupKind.Organization, pending.Kind);
            Assert.Equal("Firma", pending.CompanyName);
        }
    }
}
