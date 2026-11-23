using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class CompanyServiceTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private async Task<List<(string Name, string Permissions)>> RolesOfAsync(Guid company) =>
        (await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == company).OrderBy(r => r.Name).ToListAsync(TestContext.Current.CancellationToken)))
            .Select(r => (r.Name, string.Join(',', r.Permissions)))
            .ToList();

    private async Task<int> CompaniesNamedAsync(string name) =>
        await InDbAsync(db => db.Companies.CountAsync(c => c.Name == name, TestContext.Current.CancellationToken));

    private Task<int> CreatorRowsAsync(Guid user) =>
        InDbAsync(db => db.AuditEvents.CountAsync(a => a.Kind == AuditKinds.OrgCreated && a.ActorUserId == user, TestContext.Current.CancellationToken));

    [Fact]
    public async Task CreateForAccount_creates_the_company_the_roles_the_membership_and_the_row_in_the_callers_transaction()   // criteria 3 and 12
    {
        var cancellation = TestContext.Current.CancellationToken;
        var (user, _) = await CreatePrivateAccountAsync("maker@acme.test");
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation);

        var created = await scope.ServiceProvider.GetRequiredService<CompanyService>()
            .CreateForAccountAsync(user, "maker@acme.test", "Globex", cancellation);

        Assert.True(created.Succeeded, created.Error);
        Assert.Equal("admin", created.Value!.RoleName);
        Assert.Equal("Globex", created.Value.Name);
        var tracked = Assert.Single(db.ChangeTracker.Entries<Membership>(), e => e.Entity.CompanyId == created.Value.CompanyId).Entity;
        Assert.Equal(0, tracked.JoinedAt.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.Equal(0, await CompaniesNamedAsync("Globex"));   // another connection sees nothing before the commit
        Assert.Equal(0, await CreatorRowsAsync(user));
        await transaction.CommitAsync(cancellation);

        var company = created.Value.CompanyId;
        Assert.Equal(1, await CompaniesNamedAsync("Globex"));
        Assert.Equal([("admin", "*"), ("user", "reports:read,reports:approve")], await RolesOfAsync(company));
        var membership = await InDbAsync(d => d.Memberships.AsNoTracking().SingleAsync(m => m.CompanyId == company, cancellation));
        Assert.Equal(user, membership.UserId);
        Assert.Equal(await RoleIdAsync(company, "admin"), membership.RoleId);
        Assert.Equal(created.Value.RoleId, membership.RoleId);
        Assert.NotEqual(await RoleIdAsync(await DevCompanyIdAsync(), "admin"), membership.RoleId);
        Assert.Null(membership.LastUsedAt);
        var row = Assert.Single(await InDbAsync(d => d.AuditEvents.AsNoTracking()
            .Where(a => a.Kind == AuditKinds.OrgCreated && a.OrgId == company).ToListAsync(cancellation)));
        Assert.Equal(user, row.ActorUserId);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal("maker@acme.test", row.SubjectEmail);
        Assert.Equal("Globex", row.OrgName);
        Assert.Equal("api", AuditApi.Text(row, "via"));
    }

    [Fact]
    public async Task CreateForAccount_without_a_transaction_throws()
    {
        var (user, _) = await CreatePrivateAccountAsync("maker@acme.test");
        using var scope = Factory.Services.CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<CompanyService>()
            .CreateForAccountAsync(user, "maker@acme.test", "Globex", TestContext.Current.CancellationToken));

        Assert.Equal(0, await CompaniesNamedAsync("Globex"));
    }

    [Fact]
    public async Task CreateForAccount_with_a_bad_name_writes_nothing()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var (user, _) = await CreatePrivateAccountAsync("maker@acme.test");
        var before = await InDbAsync(db => db.Companies.CountAsync(cancellation));
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation);

        var created = await scope.ServiceProvider.GetRequiredService<CompanyService>()
            .CreateForAccountAsync(user, "maker@acme.test", " Globex", cancellation);
        await transaction.CommitAsync(cancellation);

        Assert.False(created.Succeeded);
        Assert.Equal("invalid_request", created.Error);
        Assert.Equal(before, await InDbAsync(d => d.Companies.CountAsync(cancellation)));
        Assert.Equal(0, await CreatorRowsAsync(user));
    }

    [Fact]
    public async Task CreateForAccount_rolls_back_with_the_callers_transaction()   // criterion 12
    {
        var cancellation = TestContext.Current.CancellationToken;
        var (user, _) = await CreatePrivateAccountAsync("maker@acme.test");
        var memberships = await InDbAsync(db => db.Memberships.CountAsync(cancellation));
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellation);
            var created = await scope.ServiceProvider.GetRequiredService<CompanyService>()
                .CreateForAccountAsync(user, "maker@acme.test", "Globex", cancellation);
            Assert.True(created.Succeeded, created.Error);
            await transaction.RollbackAsync(cancellation);
        }

        Assert.Equal(0, await CompaniesNamedAsync("Globex"));
        Assert.Equal(memberships, await InDbAsync(db => db.Memberships.CountAsync(cancellation)));
        Assert.Equal(0, await CreatorRowsAsync(user));
    }

    [Fact]
    public async Task CreateAsync_still_writes_the_cli_row_with_no_actor()   // criterion 12
    {
        var company = await CreateCompanyAsync("Acme");

        var row = Assert.Single(await InDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Kind == AuditKinds.OrgCreated && a.OrgId == company).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("cli", AuditApi.Text(row, "via"));
        Assert.Null(row.ActorUserId);
        Assert.Null(row.SubjectUserId);
        Assert.Null(row.SubjectEmail);
        Assert.Equal(["via"], AuditApi.DetailNames(row));
    }

    [Fact]
    public async Task Company_starts_with_a_copy_of_the_active_default_roles()   // criterion 1
    {
        var company = await CreateCompanyAsync("Acme");

        Assert.Equal([("admin", "*"), ("user", "reports:read,reports:approve")], await RolesOfAsync(company));
        var row = await InDbAsync(db => db.Companies.AsNoTracking().SingleAsync(c => c.Id == company, TestContext.Current.CancellationToken));
        Assert.Equal("Acme", row.Name);
    }

    [Fact]
    public async Task Later_changes_to_the_defaults_do_not_touch_an_existing_company()
    {
        var first = await CreateCompanyAsync("Acme");

        Holder.Set(ManifestParser.Parse("permissions: [orders:read]\ndefault_roles:\n  owner: [\"*\"]\n").Manifest!, null);
        var second = await CreateCompanyAsync("Globex");

        Assert.Equal([("admin", "*"), ("user", "reports:read,reports:approve")], await RolesOfAsync(first));
        Assert.Equal([("owner", "*")], await RolesOfAsync(second));
    }

    [Fact]
    public async Task A_default_role_that_lists_star_is_copied_as_star_alone()
    {
        Holder.Set(ManifestParser.Parse("permissions: [orders:read]\ndefault_roles:\n  owner: [orders:read, \"*\"]\n").Manifest!, null);

        var company = await CreateCompanyAsync("Acme");

        Assert.Equal([("owner", "*")], await RolesOfAsync(company));
    }

    [Fact]
    public async Task Companies_do_not_share_roles()
    {
        var first = await CreateCompanyAsync("Acme");
        var second = await CreateCompanyAsync("Globex");

        Assert.NotEqual(await RoleIdAsync(first, "admin"), await RoleIdAsync(second, "admin"));
    }

    [Fact]
    public async Task With_the_built_in_manifest_a_company_gets_one_admin_role_holding_everything()   // criterion 20
    {
        Holder.Set(Manifest.BuiltIn, "test");

        var company = await CreateCompanyAsync("Acme");

        Assert.Equal([("admin", "*")], await RolesOfAsync(company));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Acme")]
    [InlineData("Acme ")]
    [InlineData("Ac\tme")]
    public async Task A_name_that_breaks_the_rules_creates_nothing(string name)
    {
        using var scope = Factory.Services.CreateScope();
        var before = await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken));

        var created = await scope.ServiceProvider.GetRequiredService<CompanyService>().CreateAsync(name, TestContext.Current.CancellationToken);

        Assert.False(created.Succeeded);
        Assert.Equal("invalid_request", created.Error);
        Assert.Equal(before, await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
    }
}
