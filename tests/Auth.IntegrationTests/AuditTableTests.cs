using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class AuditTableTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private async Task<List<string>> StringsAsync(FormattableString sql)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Database.SqlQuery<string>(sql).ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_migration_makes_the_table_the_contract_names()   // criterion 8
    {
        var columns = await StringsAsync(
            $"""
            SELECT column_name || ':' || data_type || ':' || is_nullable AS "Value"
            FROM information_schema.columns WHERE table_name = 'audit_events' ORDER BY ordinal_position
            """);

        Assert.Equal(
            [
                "id:uuid:NO",
                "occurred_at:timestamp with time zone:NO",
                "kind:character varying:NO",
                "actor_user_id:uuid:YES",
                "subject_user_id:uuid:YES",
                "subject_email:character varying:YES",
                "org_id:uuid:YES",
                "org_name:character varying:YES",
                "target_id:uuid:YES",
                "client_ip:character varying:YES",
                "details:jsonb:YES",
            ],
            columns);
    }

    [Fact]
    public async Task The_text_columns_have_the_lengths_the_writer_cuts_to()
    {
        var lengths = await StringsAsync(
            $"""
            SELECT column_name || ':' || data_type || ':' || character_maximum_length || ':' || is_nullable AS "Value"
            FROM information_schema.columns
            WHERE table_name = 'audit_events' AND character_maximum_length IS NOT NULL ORDER BY ordinal_position
            """);

        Assert.Equal(
            [
                "kind:character varying:40:NO",
                "subject_email:character varying:256:YES",
                "org_name:character varying:100:YES",
                "client_ip:character varying:64:YES",
            ],
            lengths);
    }

    [Fact]
    public async Task The_table_has_no_foreign_key_so_that_a_row_outlives_what_it_names()
    {
        var keys = await StringsAsync(
            $"""SELECT conname AS "Value" FROM pg_constraint WHERE conrelid = 'audit_events'::regclass AND contype = 'f'""");

        Assert.Empty(keys);
    }

    [Fact]
    public async Task The_table_has_the_indexes_the_runbook_queries_and_the_pruning_use()
    {
        var indexes = await StringsAsync(
            $"""SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'audit_events' ORDER BY indexname""");

        Assert.Equal(
            [
                "IX_audit_events_actor_user_id",
                "IX_audit_events_client_ip",
                "IX_audit_events_occurred_at",
                "IX_audit_events_org_id",
                "IX_audit_events_subject_user_id",
                "PK_audit_events",
            ],
            indexes);
    }

    [Fact]
    public void The_kinds_are_the_twenty_two_of_the_contract()
    {
        Assert.Equal(
            [
                "login.succeeded", "login.failed", "login.locked", "logout", "refresh.reuse_detected",
                "password.reset_requested", "password.reset", "email.verified",
                "invite.sent", "invite.resent", "invite.accepted", "invite.cancelled",
                "member.removed", "member.role_changed", "role.created", "role.updated", "role.deleted",
                "org.created", "org.renamed", "org.deleted", "org.delete_refused", "rate_limit.hit",
            ],
            AuditKinds.All);
        Assert.All(AuditKinds.All, kind => Assert.True(kind.Length <= 40));
    }
}
