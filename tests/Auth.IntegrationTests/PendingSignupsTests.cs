using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Signup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Auth.IntegrationTests;

public sealed class PendingSignupsTests : MailTestBase
{
    private const string Normalized = "ANNA@FIRMA.TEST";
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    public PendingSignupsTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The loop of the pruning service waits on the fake clock and Clock.Advance fires its timer: a background pass would delete the
        // rows these tests count after moving the clock by a day.
        Factory.WithoutHostedService<EmailPruningService>();
    }

    private Task<int> UpsertAsync(string typed = "Anna@Firma.test", string company = "Firma", string normalized = Normalized)
    {
        var now = StorableTime.Now(Clock);
        return InDbAsync(async db =>
        {
            await PendingSignups.UpsertAsync(db, normalized, typed, company, now, TestContext.Current.CancellationToken);
            return 0;
        });
    }

    private Task<string> IssueAsync(string normalized = Normalized)
    {
        var now = StorableTime.Now(Clock);
        return InDbAsync(db => PendingSignups.IssueAsync(db, normalized, now, TestContext.Current.CancellationToken));
    }

    private Task<PendingSignup> RowAsync() =>
        InDbAsync(db => db.PendingSignups.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Upsert_stores_the_typed_address_the_company_and_24_hours()
    {
        await UpsertAsync();

        var row = await RowAsync();

        Assert.Equal(Normalized, row.NormalizedEmail);
        Assert.Equal("Anna@Firma.test", row.Email);
        Assert.Equal("Firma", row.CompanyName);
        Assert.Equal(StorableTime.Now(Clock), row.RequestedAt);
        Assert.Equal(StorableTime.Now(Clock) + Day, row.ExpiresAt);
        Assert.Null(row.TokenHash);
        Assert.Null(row.TokenExpiresAt);
    }

    [Fact]
    public async Task Upsert_for_the_same_address_replaces_the_names_and_the_time_and_keeps_the_link()
    {
        await UpsertAsync();
        var token = await IssueAsync();
        var issued = await RowAsync();
        Clock.Advance(TimeSpan.FromHours(5));

        await UpsertAsync(typed: "ANNA@firma.test", company: "Second");

        var row = await RowAsync();
        Assert.Equal("ANNA@firma.test", row.Email);
        Assert.Equal("Second", row.CompanyName);
        Assert.Equal(StorableTime.Now(Clock) + Day, row.ExpiresAt);
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);            // a failed sending of the new mail leaves the old link valid
        Assert.Equal(issued.TokenExpiresAt, row.TokenExpiresAt);           // and it does not live a minute longer than 24 hours from its own mail
    }

    [Fact]
    public async Task Issue_stores_only_the_hash_and_24_hours_from_now_for_the_link_and_the_row()
    {
        await UpsertAsync();
        Clock.Advance(TimeSpan.FromHours(1));

        var token = await IssueAsync();

        var row = await RowAsync();
        Assert.Equal(43, token.Length);
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);
        Assert.Equal(StorableTime.Now(Clock) + Day, row.TokenExpiresAt);
        Assert.Equal(StorableTime.Now(Clock) + Day, row.ExpiresAt);
    }

    [Fact]
    public async Task Issue_replaces_the_earlier_link()
    {
        await UpsertAsync();
        var first = await IssueAsync();
        var second = await IssueAsync();

        Assert.NotEqual(first, second);
        Assert.Equal(EmailTokens.HashOf(second), (await RowAsync()).TokenHash);
        Assert.Null(await InDbAsync(db => PendingSignups.FindUsableAsync(db, EmailTokens.HashOf(first), StorableTime.Now(Clock), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Consume_uses_the_link_up_once_and_removes_the_row()
    {
        await UpsertAsync();
        var token = await IssueAsync();
        var hash = EmailTokens.HashOf(token);

        var first = await InDbAsync(db => PendingSignups.ConsumeAsync(db, hash, StorableTime.Now(Clock), TestContext.Current.CancellationToken));
        var second = await InDbAsync(db => PendingSignups.ConsumeAsync(db, hash, StorableTime.Now(Clock), TestContext.Current.CancellationToken));

        Assert.NotNull(first);
        Assert.Equal("Anna@Firma.test", first.Email);
        Assert.Null(second);
        Assert.Equal(0, await InDbAsync(db => db.PendingSignups.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_link_works_until_exactly_24_hours_after_it_was_issued()
    {
        await UpsertAsync();
        var token = await IssueAsync();
        var hash = EmailTokens.HashOf(token);

        Clock.Advance(Day - TimeSpan.FromSeconds(1));
        Assert.NotNull(await InDbAsync(db => PendingSignups.FindUsableAsync(db, hash, StorableTime.Now(Clock), TestContext.Current.CancellationToken)));

        Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await InDbAsync(db => PendingSignups.FindUsableAsync(db, hash, StorableTime.Now(Clock), TestContext.Current.CancellationToken)));
        Assert.Null(await InDbAsync(db => PendingSignups.ConsumeAsync(db, hash, StorableTime.Now(Clock), TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.PendingSignups.CountAsync(TestContext.Current.CancellationToken)));   // not consumed: the clean-up removes it
    }

    [Fact]
    public async Task LockUsable_finds_a_live_row_by_address_only()
    {
        await UpsertAsync();

        var live = await InDbAsync(db => PendingSignups.LockUsableAsync(db, Normalized, StorableTime.Now(Clock), TestContext.Current.CancellationToken));
        var other = await InDbAsync(db => PendingSignups.LockUsableAsync(db, "OTHER@FIRMA.TEST", StorableTime.Now(Clock), TestContext.Current.CancellationToken));
        Clock.Advance(Day);
        var expired = await InDbAsync(db => PendingSignups.LockUsableAsync(db, Normalized, StorableTime.Now(Clock), TestContext.Current.CancellationToken));

        Assert.NotNull(live);
        Assert.Null(other);
        Assert.Null(expired);
    }

    [Fact]
    public async Task Delete_removes_the_row_of_the_address_and_no_other()
    {
        await UpsertAsync();
        await UpsertAsync(normalized: "OTHER@FIRMA.TEST", typed: "other@firma.test");

        await InDbAsync(async db =>
        {
            await PendingSignups.DeleteAsync(db, Normalized, TestContext.Current.CancellationToken);
            return 0;
        });

        var left = await InDbAsync(db => db.PendingSignups.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal("OTHER@FIRMA.TEST", left.NormalizedEmail);
    }

    [Fact]
    public async Task Upsert_never_sets_the_expiry_of_the_row_before_that_of_its_link()
    {
        await UpsertAsync();
        var earlier = StorableTime.Now(Clock);
        Clock.Advance(TimeSpan.FromHours(1));
        await IssueAsync();

        // A request that waited on the row lock while the mail was sent carries a time from before the sending.
        await InDbAsync(async db =>
        {
            await PendingSignups.UpsertAsync(db, Normalized, "Anna@Firma.test", "Second", earlier, TestContext.Current.CancellationToken);
            return 0;
        });

        var row = await RowAsync();
        Assert.Equal("Second", row.CompanyName);
        Assert.Equal(row.TokenExpiresAt, row.ExpiresAt);
    }

    [Fact]
    public async Task An_older_link_stops_at_its_own_24_hours_while_a_newer_request_keeps_the_row()
    {
        await UpsertAsync();
        var hash = EmailTokens.HashOf(await IssueAsync());
        Clock.Advance(TimeSpan.FromHours(5));
        await UpsertAsync(company: "Second");

        Clock.Advance(Day - TimeSpan.FromHours(5));   // exactly 24 hours after the mail of the old link

        Assert.Null(await InDbAsync(db => PendingSignups.FindUsableAsync(db, hash, StorableTime.Now(Clock), TestContext.Current.CancellationToken)));
        Assert.Null(await InDbAsync(db => PendingSignups.ConsumeAsync(db, hash, StorableTime.Now(Clock), TestContext.Current.CancellationToken)));
        Assert.NotNull(await InDbAsync(db => PendingSignups.LockUsableAsync(db, Normalized, StorableTime.Now(Clock), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Issue_for_an_address_without_a_row_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => IssueAsync());
    }

    [Fact]
    public async Task LockUsable_holds_the_row_until_the_transaction_ends()
    {
        await UpsertAsync();
        var cancellation = TestContext.Current.CancellationToken;
        using var scopeA = Factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transactionA = await dbA.Database.BeginTransactionAsync(cancellation);
        Assert.NotNull(await PendingSignups.LockUsableAsync(dbA, Normalized, StorableTime.Now(Clock), cancellation));

        using var scopeB = Factory.Services.CreateScope();
        var dbB = scopeB.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transactionB = await dbB.Database.BeginTransactionAsync(cancellation);
        await dbB.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '200ms'", cancellation);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => PendingSignups.DeleteAsync(dbB, Normalized, cancellation));

        var postgres = failure as PostgresException ?? failure.InnerException as PostgresException;
        Assert.NotNull(postgres);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, postgres.SqlState);
        await transactionB.RollbackAsync(cancellation);
        await transactionA.RollbackAsync(cancellation);
    }
}
