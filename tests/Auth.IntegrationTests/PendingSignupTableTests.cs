using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class PendingSignupTableTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private async Task<List<string>> StringsAsync(FormattableString sql)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Database.SqlQuery<string>(sql).ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_migration_makes_the_table()
    {
        var columns = await StringsAsync(
            $"""
            SELECT column_name || ':' || data_type || ':' || coalesce(character_maximum_length::text, '') || ':' || is_nullable AS "Value"
            FROM information_schema.columns WHERE table_name = 'PendingSignups' ORDER BY column_name
            """);

        Assert.Equal(
            [
                "CompanyName:character varying:100:YES",
                "Email:character varying:256:NO",
                "ExpiresAt:timestamp with time zone::NO",
                "Kind:smallint::NO",
                "NormalizedEmail:character varying:256:NO",
                "RequestedAt:timestamp with time zone::NO",
                "TokenExpiresAt:timestamp with time zone::YES",
                "TokenHash:bytea::YES",
            ],
            columns);
    }

    [Fact]
    public async Task The_table_has_the_indexes_the_clean_up_and_the_link_use()
    {
        var indexes = await StringsAsync(
            $"""SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'PendingSignups' ORDER BY indexname""");

        Assert.Equal(["IX_PendingSignups_ExpiresAt", "IX_PendingSignups_TokenHash", "PK_PendingSignups"], indexes);
    }

    [Fact]
    public async Task The_hash_of_a_link_is_unique()
    {
        var unique = await StringsAsync(
            $"""SELECT indexdef AS "Value" FROM pg_indexes WHERE indexname = 'IX_PendingSignups_TokenHash'""");

        Assert.Contains("UNIQUE", Assert.Single(unique), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_table_has_no_foreign_key_so_that_a_pending_sign_up_is_not_an_account()   // Decision 7
    {
        var keysOfTable = await StringsAsync(
            $"""SELECT conname AS "Value" FROM pg_constraint WHERE conrelid = '"PendingSignups"'::regclass AND contype = 'f'""");

        Assert.Empty(keysOfTable);
    }
}
