using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Provider tickets: the table <c>ProviderTickets</c> as the migration makes it.</summary>
public sealed class ProviderTicketTableTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
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
            FROM information_schema.columns WHERE table_name = 'ProviderTickets' ORDER BY column_name
            """);

        Assert.Equal(
            [
                "BindingHash:bytea::NO",
                "Email:text::YES",
                "EmailVerified:boolean::NO",
                "ExpiresAt:timestamp with time zone::NO",
                "Id:uuid::NO",
                "IdentityKey:character varying:255:NO",
                "InviteTokenHash:bytea::YES",
                "Provider:character varying:20:NO",
                "Purpose:smallint::NO",
                "TicketHash:bytea::NO",
            ],
            columns);
    }

    [Fact]
    public async Task The_table_has_a_unique_index_on_the_hash_and_one_on_the_expiry()
    {
        var indexes = await StringsAsync($"""SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'ProviderTickets' ORDER BY indexname""");
        var unique = await StringsAsync($"""SELECT indexdef AS "Value" FROM pg_indexes WHERE indexname = 'IX_ProviderTickets_TicketHash'""");

        Assert.Equal(["IX_ProviderTickets_ExpiresAt", "IX_ProviderTickets_TicketHash", "PK_ProviderTickets"], indexes);
        Assert.Contains("UNIQUE", Assert.Single(unique), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_table_has_no_foreign_key_so_that_a_ticket_names_no_account()   // spec 0011 → Provider tickets: "not tied to an account"
    {
        var foreignKeys = await StringsAsync(
            $"""SELECT conname AS "Value" FROM pg_constraint WHERE conrelid = '"ProviderTickets"'::regclass AND contype = 'f'""");

        Assert.Empty(foreignKeys);
    }
}
