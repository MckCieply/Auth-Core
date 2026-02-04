using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
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
