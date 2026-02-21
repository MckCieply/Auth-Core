using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

public sealed class AuditLogTests : TenancyTestBase
{
    public AuditLogTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithoutHostedService<AuditPruningService>();
    }

    private static AuditEntry Entry(string kind = AuditKinds.LoginFailed) => new() { Kind = kind };

    /// <summary>PostgreSQL stores <c>jsonb</c> in its own normal form, with a space after each colon.</summary>
    private static string? Compact(string? json) => json?.Replace(" ", "", StringComparison.Ordinal);

    /// <summary>The rows these tests wrote: not the <c>org.created</c> of the development company, which every host's seed writes.</summary>
    private async Task<List<AuditEvent>> RowsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().AuditEvents.AsNoTracking()
            .OrderBy(a => a.OccurredAt).ToListAsync(TestContext.Current.CancellationToken);
        return [.. rows.Where(a => a.Kind != AuditKinds.OrgCreated || Compact(a.Details) != """{"via":"seed"}""")];
    }

    [Fact]
    public async Task A_staged_row_is_written_by_the_save_of_the_change_it_belongs_to()   // criterion 8
    {
        var subject = Guid.NewGuid();
        var org = Guid.NewGuid();
        var target = Guid.NewGuid();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
            await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.MemberRemoved,
                SubjectUserId = subject,
                SubjectEmail = "worker@acme.test",
                OrgId = org,
                OrgName = "Acme",
                TargetId = target,
                Details = new Dictionary<string, object?> { ["role"] = "user" },
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        var row = Assert.Single(await RowsAsync());
        Assert.Equal("member.removed", row.Kind);
        Assert.Equal(subject, row.SubjectUserId);
        Assert.Equal("worker@acme.test", row.SubjectEmail);
        Assert.Equal(org, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(target, row.TargetId);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.ClientIp);   // no request: the operator CLI
        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime, row.OccurredAt.UtcDateTime, TimeSpan.FromMilliseconds(1));
        Assert.Equal("""{"role":"user"}""", Compact(row.Details));
    }

    [Fact]
    public async Task A_change_that_is_rolled_back_leaves_no_row()   // criterion 8: a failed change leaves no row
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
            await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            audit.Stage(Entry(AuditKinds.OrgRenamed));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task A_row_that_is_staged_and_never_saved_is_never_written()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AuditLog>().Stage(Entry());
        }

        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task The_operator_is_marked_and_a_member_is_the_actor()
    {
        var member = Guid.NewGuid();
        var tenant = new TenantContext(member, Guid.NewGuid(), "Acme", Guid.NewGuid(), "admin", [], false);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
            audit.Stage(Entry(AuditKinds.OrgCreated), Actor.Operator);
            audit.Stage(new AuditEntry { Kind = AuditKinds.OrgRenamed, Details = new Dictionary<string, object?> { ["to"] = "B" } }, Actor.Of(tenant));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var rows = await RowsAsync();
        var created = Assert.Single(rows, r => r.Kind == "org.created");
        Assert.Null(created.ActorUserId);
        Assert.Equal("""{"via":"cli"}""", Compact(created.Details));
        var renamed = Assert.Single(rows, r => r.Kind == "org.renamed");
        Assert.Equal(member, renamed.ActorUserId);
        Assert.DoesNotContain("cli", renamed.Details ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_with_no_details_stores_null()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuditLog>().WriteAloneAsync(Entry(), cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Null(Assert.Single(await RowsAsync()).Details);
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::1")]
    [InlineData(null, "unknown")]
    public async Task The_whole_client_address_is_recorded_and_unknown_when_a_request_has_none(string? remote, string expected)   // criterion 2
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
            try
            {
                await scope.ServiceProvider.GetRequiredService<AuditLog>().WriteAloneAsync(Entry(), cancellationToken: TestContext.Current.CancellationToken);
            }
            finally
            {
                scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
            }
        }

        Assert.Equal(expected, Assert.Single(await RowsAsync()).ClientIp);
    }

    [Fact]
    public async Task The_typed_address_is_cut_to_256_characters_and_the_org_name_to_100_and_no_pair_is_split()
    {
        var email = new string('a', 255) + char.ConvertFromUtf32(0x1F600) + "@example.test";   // a pair that straddles the 256th character
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuditLog>().WriteAloneAsync(
                new AuditEntry { Kind = AuditKinds.LoginFailed, SubjectEmail = email, OrgName = new string('o', 150) },
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(new string('a', 255), row.SubjectEmail);   // the high half of the pair went with the low one
        Assert.Equal(new string('o', 100), row.OrgName);
    }

    [Fact]
    public async Task A_write_on_its_own_that_fails_is_logged_and_never_thrown_and_is_not_retried()   // the write of a rate-limit hit never fails the request
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();

            await audit.WriteAloneAsync(new AuditEntry { Kind = new string('k', 100) }, cancellationToken: TestContext.Current.CancellationToken);   // longer than the column

            Assert.Equal(0, await db.SaveChangesAsync(TestContext.Current.CancellationToken));   // nothing is left to retry
        }

        Assert.Empty(await RowsAsync());
        Assert.Single(Logs.Entries, e => e.Level == LogLevel.Warning && e.Category.EndsWith("AuditLog", StringComparison.Ordinal));
        // EF Core logs its own Error lines for the failed command and the failed save; they are expected, and the Warning above is the line to look for.
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Error && e.Category == "Microsoft.EntityFrameworkCore.Database.Command");
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Error && e.Category == "Microsoft.EntityFrameworkCore.Update");
    }

    [Fact]
    public async Task A_write_on_its_own_never_saves_what_the_callers_context_is_tracking()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
        audit.Stage(new AuditEntry { Kind = AuditKinds.OrgRenamed });   // a change the caller has half made

        await audit.WriteAloneAsync(new AuditEntry { Kind = AuditKinds.LoginFailed }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([AuditKinds.LoginFailed], (await RowsAsync()).Select(r => r.Kind));   // only the event on its own is in the table
        Assert.Equal(1, await db.SaveChangesAsync(TestContext.Current.CancellationToken)); // the caller's row still waits for the caller's save
        Assert.Equal(2, (await RowsAsync()).Count);
    }

    [Fact]
    public async Task A_failed_write_on_its_own_leaves_the_callers_pending_change_alone()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
        audit.Stage(new AuditEntry { Kind = AuditKinds.OrgRenamed });

        await audit.WriteAloneAsync(new AuditEntry { Kind = new string('k', 100) }, cancellationToken: TestContext.Current.CancellationToken);   // fails: longer than the column

        Assert.Equal(1, await db.SaveChangesAsync(TestContext.Current.CancellationToken));   // the caller's own save still works
        Assert.Equal([AuditKinds.OrgRenamed], (await RowsAsync()).Select(r => r.Kind));
    }

    [Fact]
    public async Task Details_that_cannot_be_serialised_are_logged_and_never_thrown()
    {
        var loop = new Dictionary<string, object?>();
        loop["self"] = loop;   // deeper than any JSON writer goes
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuditLog>().WriteAloneAsync(
                new AuditEntry { Kind = AuditKinds.LoginFailed, Details = loop },
                cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Empty(await RowsAsync());
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Warning && e.Category.EndsWith("AuditLog", StringComparison.Ordinal));
    }

    private sealed class CancellingScopes(Func<OperationCanceledException> make) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw make();
    }

    private AuditLog LogWithScopes(IServiceScope scope, IServiceScopeFactory scopes) =>
        new(
            scope.ServiceProvider.GetRequiredService<AuthDbContext>(),
            scopes,
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>(),
            Clock,
            scope.ServiceProvider.GetRequiredService<ILogger<AuditLog>>());

    [Fact]
    public async Task A_cancellation_that_did_not_come_from_the_callers_token_is_swallowed()
    {
        using var scope = Factory.Services.CreateScope();
        var audit = LogWithScopes(scope, new CancellingScopes(() => new OperationCanceledException("a timeout inside the store")));

        await audit.WriteAloneAsync(Entry(), cancellationToken: TestContext.Current.CancellationToken);   // does not throw

        Assert.Empty(await RowsAsync());
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Warning && e.Category.EndsWith("AuditLog", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_callers_own_cancelled_token_is_the_one_exception_that_is_thrown()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var scope = Factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => audit.WriteAloneAsync(Entry(), cancellationToken: cancelled.Token));

        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task The_id_and_the_time_of_a_row_come_from_the_injected_clock()
    {
        var moment = new DateTimeOffset(2030, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);
        Clock.SetUtcNow(moment);
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuditLog>().WriteAloneAsync(Entry(), cancellationToken: TestContext.Current.CancellationToken);
        }

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(moment, row.OccurredAt.ToUniversalTime());
        var bytes = row.Id.ToByteArray(bigEndian: true);   // a version 7 id begins with the Unix time in milliseconds
        long milliseconds = 0;
        for (var i = 0; i < 6; i++)
        {
            milliseconds = (milliseconds << 8) | bytes[i];
        }

        Assert.Equal(moment.ToUnixTimeMilliseconds(), milliseconds);
    }

    [Fact]
    public async Task The_details_are_queryable_as_json()   // the backup runbook asks details->>'reason'
    {
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuditLog>().WriteAloneAsync(
                new AuditEntry { Kind = AuditKinds.LoginFailed, Details = new Dictionary<string, object?> { ["reason"] = "wrong_password" } },
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var reasons = await InDbAsync(db => db.Database
            .SqlQuery<string>($"""SELECT details->>'reason' AS "Value" FROM audit_events WHERE kind = 'login.failed'""")
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["wrong_password"], reasons);
    }
}
