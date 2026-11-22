using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0015 → Data: <c>Memberships.LastUsedAt</c> (nullable, null for every row that exists before any sign-in writes it) and the test
/// helpers that give an account several memberships and hold their rows.
/// </summary>
public sealed class MembershipLastUsedTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private sealed class ColumnInfo
    {
        public string DataType { get; init; } = "";

        public string IsNullable { get; init; } = "";

        public string? ColumnDefault { get; init; }
    }

    private async Task<ColumnInfo?> ReadColumnAsync()
    {
        _ = Factory.Services;
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var rows = await db.Database
            .SqlQuery<ColumnInfo>($"""
                SELECT data_type AS "DataType", is_nullable AS "IsNullable", column_default AS "ColumnDefault"
                FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = 'Memberships' AND column_name = 'LastUsedAt'
                """)
            .ToListAsync(TestContext.Current.CancellationToken);
        return rows.SingleOrDefault();
    }

    private async Task<Guid> SeedUserIdAsync()
    {
        _ = Factory.Services;
        return await InDbAsync(db => db.Users.Where(u => u.Email == Factory.SeedEmail).Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken));
    }

    private Task<List<(Guid CompanyId, Guid RoleId)>> MembershipsOfAsync(Guid userId) =>
        InDbAsync(async db => (await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.CompanyId)
            .Select(m => new { m.CompanyId, m.RoleId })
            .ToListAsync(TestContext.Current.CancellationToken))
            .Select(m => (m.CompanyId, m.RoleId))
            .ToList());

    [Fact]
    public async Task The_column_is_a_nullable_timestamptz()
    {
        var column = await ReadColumnAsync();

        Assert.NotNull(column);
        Assert.Equal("timestamp with time zone", column.DataType);
        Assert.Equal("YES", column.IsNullable);
        Assert.Null(column.ColumnDefault);
    }

    [Fact]
    public async Task Existing_memberships_read_null_after_the_migration()
    {
        var user = await SeedUserIdAsync();
        var company = await DevCompanyIdAsync();

        Assert.Null(await LastUsedAtAsync(user, company));   // written before any sign-in
        var row = await InDbAsync(db => db.Memberships.AsNoTracking().SingleAsync(m => m.UserId == user && m.CompanyId == company, TestContext.Current.CancellationToken));
        Assert.Equal(await RoleIdAsync(company, "admin"), row.RoleId);
        Assert.Equal(StorableTime.Now(Clock), row.JoinedAt);
    }

    [Fact]
    public async Task The_helpers_add_and_remove_one_membership_of_several()
    {
        var (user, space) = await CreatePrivateAccountAsync("ola@poczta.test");
        var first = await CreateCompanyAsync("Acme");
        var second = await CreateCompanyAsync("Beta");

        await AddMembershipAsync(user, first, "user");
        await AddMembershipAsync(user, second, "admin", StorableTime.Now(Clock).AddDays(2));
        var added = await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.UserId == user).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, added.Count);
        Assert.Equal(await RoleIdAsync(first, "user"), added.Single(m => m.CompanyId == first).RoleId);
        Assert.Equal(StorableTime.Now(Clock).AddHours(1), added.Single(m => m.CompanyId == first).JoinedAt);   // the default
        Assert.Equal(await RoleIdAsync(second, "admin"), added.Single(m => m.CompanyId == second).RoleId);
        Assert.Equal(StorableTime.Now(Clock).AddDays(2), added.Single(m => m.CompanyId == second).JoinedAt);   // the one given
        Assert.Null(await LastUsedAtAsync(user, first));
        Assert.Null(await LastUsedAtAsync(user, second));

        var spaceRole = (await MembershipsOfAsync(user)).Single(m => m.CompanyId == space).RoleId;
        var secondRole = await RoleIdAsync(second, "admin");

        await SetMemberRoleAsync(user, first, "admin");
        var changed = await MembershipsOfAsync(user);
        Assert.Equal(await RoleIdAsync(first, "admin"), changed.Single(m => m.CompanyId == first).RoleId);
        Assert.Equal(spaceRole, changed.Single(m => m.CompanyId == space).RoleId);
        Assert.Equal(secondRole, changed.Single(m => m.CompanyId == second).RoleId);

        await RemoveMembershipAsync(user, first);
        var left = await MembershipsOfAsync(user);
        Assert.Equal(new[] { space, second }.Order(), left.Select(m => m.CompanyId).Order());
    }

    [Fact]
    public async Task A_held_membership_row_lets_the_account_row_be_locked()
    {
        var (user, space) = await CreatePrivateAccountAsync("ola@poczta.test");
        await using var membershipRow = await HeldLock.MembershipRowAsync(Factory, user, space);
        await using var other = await HeldLock.AddressAsync(Factory, "unrelated@poczta.test");

        await other.LockAccountAsync(user);   // throws on the two-second lock timeout: the helpers took no account lock

        // and the held row is really held: the same transaction (its lock_timeout is now 2 s) cannot delete that membership
        // (the context's retry strategy wraps the lock timeout in an InvalidOperationException, so the PostgresException is its base)
        var blocked = await Assert.ThrowsAnyAsync<Exception>(() => other.DeleteMembershipAsync(user, space));
        Assert.Equal("55P03", Assert.IsType<PostgresException>(blocked.GetBaseException()).SqlState);
    }
}
