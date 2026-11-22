using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

/// <summary>Spec 0016 → a host that sets no <c>Auth:Tenancy:MaxCompaniesPerAccount</c> allows 20 companies to an account.</summary>
public sealed class OrgCreateDefaultLimitTests : AuditTestBase
{
    public OrgCreateDefaultLimitTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithOrgCreation().WithoutSetting(CompanyLimitSettings.Key);
    }

    [Fact]
    public async Task The_default_limit_is_20()   // criterion 9
    {
        var (_, userId, _) = await CompanyWithAdminAsync();

        // 18 more memberships (19 in all) in one database round trip.
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            for (var i = 0; i < 18; i++)
            {
                var company = new Company { Id = Guid.NewGuid(), Name = $"Filler {i}", Kind = CompanyKind.Organization, CreatedAt = now };
                var role = new CompanyRole { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "admin", NormalizedName = "ADMIN", Permissions = ["*"] };
                db.Companies.Add(company);
                db.CompanyRoles.Add(role);
                db.Memberships.Add(new Membership { UserId = userId, CompanyId = company.Id, RoleId = role.Id, JoinedAt = now.AddHours(1) });
            }

            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        Assert.Equal(19, await CompanyCountOfAsync(userId));
        var session = await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword);

        using (var twentieth = await TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Twentieth" }))
        {
            await TenancyApi.ReadCreatedAsync(twentieth);
        }

        using var next = await TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Twenty-first" });
        await TenancyApi.AssertErrorAsync(next, HttpStatusCode.Conflict, "company_limit_reached");
        Assert.Equal(20, await CompanyCountOfAsync(userId));
    }
}
