using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Upgrade: at start, every account without a membership is deleted, once, in the service only.</summary>
public sealed class AccountsWithoutSpaceStartupTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static async Task<Guid> CreateUserAsync(AuthAppFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, "Another-Passw0rd");
        Assert.True(result.Succeeded);
        return user.Id;
    }

    private static async Task<T> InDbAsync<T>(AuthAppFactory factory, Func<AuthDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private static Task<List<AuditEvent>> DeletedRowsAsync(AuthAppFactory factory) =>
        InDbAsync(factory, db => db.AuditEvents.AsNoTracking().Where(a => a.Kind == AuditKinds.AccountDeleted).ToListAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_start_deletes_every_account_without_a_membership_once_and_keeps_the_seed_users()   // criterion 25
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        Guid lonely;
        Guid gone;
        await using (var first = new AuthAppFactory(postgres, keys, database))
        {
            lonely = await CreateUserAsync(first, "lonely@example.test");   // made while the host runs: an earlier version's left-overs
            gone = await CreateUserAsync(first, "gone@example.test");
        }

        await using (var second = new AuthAppFactory(postgres, keys, database))
        {
            var rows = await DeletedRowsAsync(second);
            Assert.Equal(new[] { lonely, gone }.Order(), rows.Select(r => r.SubjectUserId!.Value).Order());
            Assert.All(rows, row =>
            {
                Assert.Null(row.ActorUserId);
                Assert.Null(row.ClientIp);
                Assert.Null(row.OrgId);
                Assert.Equal("upgrade", AuditApi.Text(row, "reason"));
            });
            Assert.Equal(0, await InDbAsync(second, db => db.Users.CountAsync(u => u.Id == lonely || u.Id == gone, TestContext.Current.CancellationToken)));
            var seed = await second.SeedUserIdAsync();
            Assert.Equal(1, await InDbAsync(second, db => db.Memberships.CountAsync(m => m.UserId == seed, TestContext.Current.CancellationToken)));
        }

        await using (var third = new AuthAppFactory(postgres, keys, database))
        {
            Assert.Equal(2, (await DeletedRowsAsync(third)).Count);   // a second start changes nothing
            var seed = await third.SeedUserIdAsync();
            Assert.Equal(1, await InDbAsync(third, db => db.Memberships.CountAsync(m => m.UserId == seed, TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task The_operator_cli_never_runs_the_step()   // spec 0010 → Upgrade: in the service only
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var lonely = await CreateUserAsync(factory, "lonely@example.test");

        var run = await OperatorCli.RunAsync(factory, "list-orgs");

        Assert.Equal(AdminCli.Done, run.Exit);
        Assert.Equal(1, await InDbAsync(factory, db => db.Users.CountAsync(u => u.Id == lonely, TestContext.Current.CancellationToken)));
        Assert.Empty(await DeletedRowsAsync(factory));
    }

    [Fact]
    public async Task Two_runs_at_once_delete_each_account_once()   // two replicas starting together
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        List<Guid> lonely = [];
        for (var i = 0; i < 8; i++)
        {
            lonely.Add(await CreateUserAsync(factory, $"lonely{i}@example.test"));
        }

        var cleanup = factory.Services.GetRequiredService<AccountsWithoutSpaceCleanup>();
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource();
        var runs = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            return await cleanup.RunAsync(ct);
        }, ct)).ToArray();
        gate.SetResult();
        var counts = await Task.WhenAll(runs);

        Assert.Equal(lonely.Count, counts.Sum());
        var rows = await DeletedRowsAsync(factory);
        Assert.Equal(lonely.Order(), rows.Select(r => r.SubjectUserId!.Value).Order());
        Assert.Equal(0, await InDbAsync(factory, db => db.Users.CountAsync(u => lonely.Contains(u.Id), ct)));
    }

    [Fact]
    public async Task An_account_that_gets_a_space_while_the_step_waits_for_its_address_is_kept()   // the membership is read again under the lock
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var ct = TestContext.Current.CancellationToken;
        var lonely = await CreateUserAsync(factory, "lonely@example.test");
        var address = await InDbAsync(factory, db => db.Users.AsNoTracking().Where(u => u.Id == lonely).Select(u => u.NormalizedEmail!).SingleAsync(ct));
        var cleanup = factory.Services.GetRequiredService<AccountsWithoutSpaceCleanup>();

        // Another flow holds the address lock; the step lists the account, then waits for the lock.
        using var scope = factory.Services.CreateScope();
        var holder = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await holder.Database.BeginTransactionAsync(ct);
        await AddressLock.AcquireAsync(holder, address, ct);

        var run = Task.Run(() => cleanup.RunAsync(ct), ct);
        await WaitForAdvisoryLockWaiterAsync(factory, ct);

        var company = new Company { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = StorableTime.Now(TimeProvider.System) };
        var role = new CompanyRole { CompanyId = company.Id, Name = "admin", NormalizedName = "ADMIN", Permissions = ["*"] };
        holder.Companies.Add(company);
        holder.CompanyRoles.Add(role);
        await holder.SaveChangesAsync(ct);
        holder.Memberships.Add(new Membership { UserId = lonely, CompanyId = company.Id, RoleId = role.Id, JoinedAt = StorableTime.Now(TimeProvider.System) });
        await holder.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        Assert.Equal(0, await run);
        Assert.Equal(1, await InDbAsync(factory, db => db.Users.CountAsync(u => u.Id == lonely, ct)));
        Assert.Empty(await DeletedRowsAsync(factory));
    }

    private static async Task WaitForAdvisoryLockWaiterAsync(AuthAppFactory factory, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())",
            connection);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The step never waited for the address lock.");
            await Task.Delay(50, cancellationToken);
        }
    }
}
