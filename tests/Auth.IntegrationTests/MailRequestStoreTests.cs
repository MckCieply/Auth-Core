using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class MailRequestStoreTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private const string Address = "SOMEONE@EXAMPLE.COM";

    private MailRequestStore Store => Factory.Services.GetRequiredService<MailRequestStore>();

    private Task<MailLimitDecision> SubmitAsync(MailKind kind, string address = Address) =>
        Store.SubmitAsync(kind, address, TestContext.Current.CancellationToken);

    private async Task<List<MailRequest>> QueueAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.MailRequests.AsNoTracking().OrderBy(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<MailRequestLimit>> LimitsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.MailRequestLimits.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Accepted_request_writes_the_limit_and_one_queue_row_due_at_once()
    {
        var decision = await SubmitAsync(MailKind.PasswordReset);

        Assert.True(decision.Allowed);
        var row = Assert.Single(await QueueAsync());
        Assert.Equal(MailKind.PasswordReset, row.Kind);
        Assert.Equal(Address, row.NormalizedEmail);
        Assert.Equal(0, row.Attempts);
        Assert.Equal(row.RequestedAt, row.NextAttemptAt);
        var limit = Assert.Single(await LimitsAsync());
        Assert.Equal(32, limit.IdentifierHash.Length);
        Assert.Equal(1, limit.WindowCount);
    }

    [Fact]
    public async Task Refused_request_writes_nothing()
    {
        await SubmitAsync(MailKind.PasswordReset);
        Clock.Advance(TimeSpan.FromSeconds(20));

        var refused = await SubmitAsync(MailKind.PasswordReset);

        Assert.False(refused.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(40), refused.RetryAfter);
        Assert.Single(await QueueAsync());
        Assert.Equal(1, Assert.Single(await LimitsAsync()).WindowCount);
    }

    [Fact]
    public async Task Kinds_and_addresses_have_limits_of_their_own()   // criterion 9
    {
        Assert.True((await SubmitAsync(MailKind.PasswordReset)).Allowed);
        Assert.True((await SubmitAsync(MailKind.EmailVerification)).Allowed);
        Assert.True((await SubmitAsync(MailKind.PasswordReset, "OTHER@EXAMPLE.COM")).Allowed);

        Assert.Equal(3, (await QueueAsync()).Count);
        Assert.Equal(3, (await LimitsAsync()).Count);
    }

    [Fact]
    public async Task Parallel_requests_for_one_address_let_exactly_one_through()
    {
        // Twelve, not more: every waiting caller holds a server connection (see LoginStreakStoreTests).
        var decisions = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Task.Run(() => SubmitAsync(MailKind.PasswordReset))));

        Assert.Equal(1, decisions.Count(d => d.Allowed));
        Assert.Single(await QueueAsync());
    }

    [Fact]
    public async Task Stored_instants_read_back_equal()
    {
        // PostgreSQL keeps microseconds (10 ticks): make sure the clock sits between two of them, or this proves nothing.
        Clock.Advance(TimeSpan.FromTicks(17 - (Clock.GetUtcNow().Ticks % 10)));
        await SubmitAsync(MailKind.PasswordReset);

        Clock.Advance(TimeSpan.FromSeconds(20));
        var refused = await SubmitAsync(MailKind.PasswordReset);

        // Exactly forty seconds: the store must compute on what the row can hold.
        Assert.False(refused.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(40), refused.RetryAfter);
    }
}
