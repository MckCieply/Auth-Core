using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// A lock that a test holds in a transaction of its own until <see cref="ReleaseAsync"/> (or, failing that, until it is disposed: rolled back),
/// and the wait for the requests queued behind locks, so that a race runs in the order the test names (the pattern of
/// <c>SignupCompleteRaceTests</c>).
/// </summary>
public sealed class HeldLock : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly IServiceScope _scope;
    private readonly AuthDbContext _db;
    private readonly IDbContextTransaction _transaction;

    private HeldLock(IServiceScope scope, AuthDbContext db, IDbContextTransaction transaction)
    {
        _scope = scope;
        _db = db;
        _transaction = transaction;
    }

    /// <summary>The advisory lock of an address, which completion, acceptance, self-deletion and the start-up step take.</summary>
    public static async Task<HeldLock> AddressAsync(AuthAppFactory factory, string normalizedEmail)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({normalizedEmail}, 0))", TestContext.Current.CancellationToken);
        return new HeldLock(scope, db, transaction);
    }

    /// <summary>A lock on the audit table that stops every write to it: a change that reaches its audit rows waits there, not committed.</summary>
    public static async Task<HeldLock> AuditTableAsync(AuthAppFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE audit_events IN SHARE MODE", TestContext.Current.CancellationToken);
        return new HeldLock(scope, db, transaction);
    }

    /// <summary>
    /// A lock on the memberships table that stops every read of it, so that a request which has read an account waits before it reads the
    /// account's space; <see cref="DeleteAccountAsync"/> then changes the rows in the same transaction, and they commit with the release.
    /// </summary>
    public static async Task<HeldLock> MembershipsAsync(AuthAppFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync("""LOCK TABLE "Memberships" IN ACCESS EXCLUSIVE MODE""", TestContext.Current.CancellationToken);
        return new HeldLock(scope, db, transaction);
    }

    /// <summary>Deletes an account, with the rows that go with it by cascade, in the transaction that holds the lock.</summary>
    public Task<int> DeleteAccountAsync(Guid userId) =>
        _db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(TestContext.Current.CancellationToken);

    /// <summary>Waits until exactly this many backends of the factory's database are waiting for a lock.</summary>
    public static async Task WaitForWaitersAsync(AuthAppFactory factory, int count)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            long waiting;
            using (var scope = factory.Services.CreateScope())
            {
                waiting = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database
                    .SqlQuery<long>($"""SELECT count(*) AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""")
                    .SingleAsync(TestContext.Current.CancellationToken);
            }

            if (waiting == count)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Expected {count} backends waiting for a lock, saw {waiting}.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    public Task ReleaseAsync() => _transaction.CommitAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        _scope.Dispose();
    }
}
