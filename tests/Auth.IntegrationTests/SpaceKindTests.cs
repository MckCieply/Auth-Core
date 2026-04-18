using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Seeding;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Spaces: the kind of a row of the companies table, and of a pending sign-up.</summary>
public sealed class SpaceKindTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private Task<List<string>> ColumnsAsync(string table) =>
        InDbAsync(db => db.Database.SqlQuery<string>(
            $"""
            SELECT column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default, '') AS "Value"
            FROM information_schema.columns WHERE table_name = {table} AND column_name = 'Kind'
            """).ToListAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Companies_have_a_kind_that_defaults_to_organization()
    {
        Assert.Equal(["Kind:smallint:NO:1"], await ColumnsAsync("Companies"));
    }

    [Fact]
    public async Task Pending_sign_ups_have_a_kind_that_defaults_to_organization()
    {
        Assert.Equal(["Kind:smallint:NO:1"], await ColumnsAsync("PendingSignups"));
    }

    [Fact]
    public async Task A_pending_sign_up_added_without_a_kind_is_an_organization_sign_up()
    {
        var now = StorableTime.Now(Clock);

        await InDbAsync(async db =>
        {
            db.PendingSignups.Add(new PendingSignup
            {
                NormalizedEmail = "ANNA@FIRMA.TEST",
                Email = "anna@firma.test",
                CompanyName = "Firma",
                RequestedAt = now,
                ExpiresAt = now.AddDays(1),
            });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        var pending = await InDbAsync(db => db.PendingSignups.AsNoTracking().SingleAsync(p => p.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken));
        Assert.Equal(SignupKind.Organization, pending.Kind);
    }

    [Fact]
    public async Task A_company_made_as_before_is_an_organization()
    {
        var company = await CreateCompanyAsync("Acme");

        var made = await InDbAsync(db => db.Companies.AsNoTracking().Where(c => c.Id == company).Select(c => c.Kind).SingleAsync(TestContext.Current.CancellationToken));
        var seeded = await InDbAsync(db => db.Companies.AsNoTracking().Where(c => c.Name == DevUserSeeder.DefaultOrgName).Select(c => c.Kind).SingleAsync(TestContext.Current.CancellationToken));

        Assert.Equal(CompanyKind.Organization, made);
        Assert.Equal(CompanyKind.Organization, seeded);
    }

    [Fact]
    public async Task A_private_account_has_a_personal_space_named_private_with_one_empty_role_and_one_member()
    {
        var (userId, spaceId) = await CreatePrivateAccountAsync("ola@poczta.test");

        var space = await InDbAsync(db => db.Companies.AsNoTracking().SingleAsync(c => c.Id == spaceId, TestContext.Current.CancellationToken));
        Assert.Equal(CompanyKind.Personal, space.Kind);
        Assert.Equal("private", space.Name);
        var role = Assert.Single(await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == spaceId).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("private", role.Name);
        Assert.Equal("PRIVATE", role.NormalizedName);
        Assert.Empty(role.Permissions);
        var member = Assert.Single(await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.CompanyId == spaceId).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(userId, member.UserId);
        Assert.Equal(role.Id, member.RoleId);
    }
}
