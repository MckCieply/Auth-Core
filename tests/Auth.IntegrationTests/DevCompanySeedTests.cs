using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class DevCompanySeedTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string UnverifiedEmail = "new@example.com";
    private const string UnverifiedPassword = "Dev-Unverified-Passw0rd";

    private static string UniqueDatabase() => "auth_" + Guid.NewGuid().ToString("N");

    private AuthAppFactory Factory(string? database = null) => new(postgres, keys, database);

    private static AuthAppFactory WithSecondUser(AuthAppFactory factory) => factory
        .WithSetting(DevUserSeeder.UnverifiedEmailKey, UnverifiedEmail)
        .WithSetting(DevUserSeeder.UnverifiedPasswordKey, UnverifiedPassword);

    private static async Task<T> InDbAsync<T>(AuthAppFactory factory, Func<AuthDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private static async Task<(string Company, string Role)?> MembershipOfAsync(AuthAppFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            return null;
        }

        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var row = await (
            from m in db.Memberships
            join c in db.Companies on m.CompanyId equals c.Id
            join r in db.CompanyRoles on m.RoleId equals r.Id
            where m.UserId == user.Id
            select new { c.Name, RoleName = r.Name })
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
        return row is null ? null : (row.Name, row.RoleName);
    }

    [Fact]
    public async Task First_seed_user_is_the_admin_of_the_development_company()
    {
        await using var factory = Factory();
        _ = factory.Services;

        Assert.Equal(("Development", "admin"), await MembershipOfAsync(factory, factory.SeedEmail));
        var roles = await InDbAsync(factory, db => db.CompanyRoles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["admin", "user"], roles.Select(r => r.Name));   // a copy of the default roles
    }

    [Fact]
    public async Task Company_name_is_a_setting()
    {
        await using var factory = Factory().WithSetting(DevUserSeeder.OrgNameKey, "Acme Dev");
        _ = factory.Services;

        Assert.Equal(("Acme Dev", "admin"), await MembershipOfAsync(factory, factory.SeedEmail));
    }

    [Fact]
    public async Task Second_seed_user_is_a_member_with_a_role_that_does_not_manage_members()
    {
        await using var factory = WithSecondUser(Factory());
        _ = factory.Services;

        Assert.Equal(("Development", "user"), await MembershipOfAsync(factory, UnverifiedEmail));
        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(UnverifiedEmail);
        Assert.False(user!.EmailConfirmed);   // it stays unconfirmed: the verification flow needs one
    }

    [Fact]
    public async Task When_every_default_role_manages_members_the_second_user_gets_an_empty_role_of_its_own()
    {
        await using var factory = WithSecondUser(Factory()).WithManifest("default_roles:\n  owner: [\"*\"]\n");
        _ = factory.Services;

        Assert.Equal(("Development", "member"), await MembershipOfAsync(factory, UnverifiedEmail));
        var member = await InDbAsync(factory, db => db.CompanyRoles.AsNoTracking().SingleAsync(r => r.Name == "member", TestContext.Current.CancellationToken));
        Assert.Empty(member.Permissions);
        Assert.Equal(("Development", "owner"), await MembershipOfAsync(factory, factory.SeedEmail));   // the first user takes a role that manages
    }

    [Fact]
    public async Task Second_seed_user_gets_the_first_role_by_name_that_holds_neither_members_manage_nor_star()   // spec 0008 → Fixes
    {
        await using var factory = WithSecondUser(Factory())
            .WithManifest("permissions: [a:b]\ndefault_roles:\n  owner: [\"*\"]\n  zeta: [a:b]\n  alpha: [a:b]\n");
        _ = factory.Services;

        // The roles are read from the database and ordered by name, not taken in the order of the file: alpha, not zeta.
        Assert.Equal(("Development", "alpha"), await MembershipOfAsync(factory, UnverifiedEmail));
        Assert.Equal(("Development", "owner"), await MembershipOfAsync(factory, factory.SeedEmail));
    }

    [Fact]
    public async Task Second_seed_user_takes_a_role_the_company_has_not_one_the_manifest_names()   // read from the database, not from the manifest
    {
        var database = UniqueDatabase();
        await using var first = Factory(database);
        _ = first.Services;   // the company is made with admin and user

        await using var second = WithSecondUser(Factory(database))
            .WithManifest("permissions: [a:b]\ndefault_roles:\n  boss: [\"*\"]\n  zeta: [a:b]\n");
        _ = second.Services;

        Assert.Equal(("Development", "user"), await MembershipOfAsync(second, UnverifiedEmail));   // zeta is only in the manifest
    }

    [Fact]
    public async Task A_role_that_holds_members_manage_or_star_is_never_the_second_users()
    {
        await using var factory = WithSecondUser(Factory())
            .WithManifest("permissions: [a:b]\ndefault_roles:\n  aaa: [members:manage]\n  bbb: [\"*\"]\n  ccc: [a:b]\n");
        _ = factory.Services;

        Assert.Equal(("Development", "ccc"), await MembershipOfAsync(factory, UnverifiedEmail));
    }

    [Fact]
    public async Task A_restart_changes_nothing()
    {
        var database = UniqueDatabase();
        await using var first = WithSecondUser(Factory(database));
        _ = first.Services;
        await using var second = WithSecondUser(Factory(database));
        _ = second.Services;

        Assert.Equal(1, await InDbAsync(second, db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(2, await InDbAsync(second, db => db.CompanyRoles.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(2, await InDbAsync(second, db => db.Memberships.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_seed_user_without_a_membership_joins_at_the_next_start()   // development databases made before spec 0005
    {
        var database = UniqueDatabase();
        await using var first = Factory(database);
        _ = first.Services;
        await InDbAsync(first, db => db.Database.ExecuteSqlAsync($"""DELETE FROM "Memberships" """, TestContext.Current.CancellationToken));

        await using var second = Factory(database);
        _ = second.Services;

        Assert.Equal(("Development", "admin"), await MembershipOfAsync(second, second.SeedEmail));
    }

    [Fact]
    public async Task A_member_is_not_moved_to_another_company_by_a_new_setting()
    {
        var database = UniqueDatabase();
        await using var first = Factory(database);
        _ = first.Services;

        await using var second = Factory(database).WithSetting(DevUserSeeder.OrgNameKey, "Elsewhere");
        _ = second.Services;

        Assert.Equal(("Development", "admin"), await MembershipOfAsync(second, second.SeedEmail));
        Assert.Equal(2, await InDbAsync(second, db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Production_host_creates_no_company()
    {
        await using var factory = Factory().WithEnvironment("Production");
        _ = factory.Services;

        Assert.Equal(0, await InDbAsync(factory, db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Without_seed_users_there_is_no_company()
    {
        await using var factory = Factory().WithSetting(DevUserSeeder.EmailKey, "").WithSetting(DevUserSeeder.PasswordKey, "");
        _ = factory.Services;

        Assert.Equal(0, await InDbAsync(factory, db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
    }
}
