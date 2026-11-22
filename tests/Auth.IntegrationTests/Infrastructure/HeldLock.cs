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
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

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

    /// <summary>The row lock of a company (<c>CompanyLock</c>), which every change to its members, roles and invitations takes first.</summary>
    public static async Task<HeldLock> CompanyAsync(AuthAppFactory factory, Guid companyId)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Companies" WHERE "Id" = {companyId} FOR UPDATE""", TestContext.Current.CancellationToken);
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

    /// <summary>
    /// The strongest row lock of one membership (<c>FOR UPDATE</c>, as a removal takes it); it blocks the weaker row locks of a sign-in.
    /// </summary>
    public static async Task<HeldLock> MembershipRowAsync(AuthAppFactory factory, Guid userId, Guid companyId)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Memberships" WHERE "UserId" = {userId} AND "CompanyId" = {companyId} FOR UPDATE""", TestContext.Current.CancellationToken);
        return new HeldLock(scope, db, transaction);
    }

    /// <summary>
    /// The row lock of one account (<c>FOR UPDATE</c>) and nothing else: a routine that has locked the memberships waits only when it reaches
    /// the account row, which is where the recount of a removal runs.
    /// </summary>
    public static async Task<HeldLock> AccountRowAsync(AuthAppFactory factory, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""", TestContext.Current.CancellationToken);
        return new HeldLock(scope, db, transaction);
    }

    /// <summary>Inserts one membership in the transaction that holds the lock: what a creation of a company leaves, committed with the release.</summary>
    public async Task InsertMembershipAsync(Guid userId, Guid companyId, Guid roleId)
    {
        var now = DateTimeOffset.UtcNow;
        _db.Memberships.Add(new Membership { UserId = userId, CompanyId = companyId, RoleId = roleId, JoinedAt = now.AddTicks(-(now.Ticks % 10)) });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Locks the row of an account (<c>FOR UPDATE</c>) in the transaction that holds the lock, waiting at most two seconds: it throws
    /// when the row is held by another transaction, so a test that must not wait for it fails fast.
    /// </summary>
    public async Task LockAccountAsync(Guid userId)
    {
        await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", TestContext.Current.CancellationToken);
        await _db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""", TestContext.Current.CancellationToken);
    }

    /// <summary>Deletes one membership in the transaction that holds the lock.</summary>
    public Task<int> DeleteMembershipAsync(Guid userId, Guid companyId) =>
        _db.Memberships.Where(m => m.UserId == userId && m.CompanyId == companyId).ExecuteDeleteAsync(TestContext.Current.CancellationToken);

    /// <summary>Gives one membership another <c>JoinedAt</c> in the transaction that holds the lock: what a removal and a new join of the same person leave.</summary>
    public Task<int> RemakeMembershipAsync(Guid userId, Guid companyId) =>
        _db.Memberships.Where(m => m.UserId == userId && m.CompanyId == companyId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.JoinedAt, m => m.JoinedAt.AddHours(1)), TestContext.Current.CancellationToken);

    /// <summary>Deletes an account, with the rows that go with it by cascade, in the transaction that holds the lock.</summary>
    public Task<int> DeleteAccountAsync(Guid userId) =>
        _db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// Waits until exactly this many backends of the factory's database are waiting for a lock. <paramref name="pending"/> are the requests the
    /// test has sent and expects to be queued behind a lock: if one of them has finished before the count is reached it never queued, and the
    /// test fails at once with its status and body instead of waiting for the patience to run out.
    /// </summary>
    public static async Task WaitForWaitersAsync(AuthAppFactory factory, int count, params Task<HttpResponseMessage>[] pending)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(pending);

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

            if (pending.FirstOrDefault(request => request.IsCompleted) is { } finished)
            {
                Assert.Fail($"Expected {count} backends waiting for a lock, saw {waiting}, and a request finished without queueing: {await DescribeAsync(finished)}");
            }

            Assert.True(DateTime.UtcNow < deadline, $"Expected {count} backends waiting for a lock, saw {waiting}.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// How many backends of the factory's database wait for the lock of a row (a transaction id) and not for a table: a request that queues
    /// behind a held row lock, as opposed to one that queues behind <see cref="AuditTableAsync"/>.
    /// </summary>
    public static async Task<long> RowLockWaitersAsync(AuthAppFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database
            .SqlQuery<long>($"""
                SELECT count(*) AS "Value" FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                WHERE NOT l.granted AND l.locktype = 'transactionid' AND a.datname = current_database()
                """)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> DescribeAsync(Task<HttpResponseMessage> finished)
    {
        if (finished.IsFaulted || finished.IsCanceled)
        {
            return finished.Exception?.GetBaseException().ToString() ?? "canceled";
        }

        var response = finished.Result;
        return $"{(int)response.StatusCode} {response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}";
    }

    public Task ReleaseAsync() => _transaction.CommitAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        _scope.Dispose();
    }
}
