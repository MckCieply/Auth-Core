using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Seeding;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Base of the tests about companies, members and roles: the mail host of <see cref="MailTestBase"/> (invitations
/// are mails), with helpers that make companies and members straight in the database. The test host runs in
/// Development, so its seed user is the <c>admin</c> of the company <see cref="DevSeedTests"/> names
/// <see cref="DevUserSeeder.DefaultOrgName"/>.
/// </summary>
public abstract class TenancyTestBase : MailTestBase
{
    protected TenancyTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
    }

    protected ManifestHolder Holder => Factory.Services.GetRequiredService<ManifestHolder>();

    /// <summary>Makes a company the way the operator does, with a copy of the active default roles.</summary>
    protected async Task<Guid> CreateCompanyAsync(string name = "Acme")
    {
        using var scope = Factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<CompanyService>().CreateAsync(name, TestContext.Current.CancellationToken);
        Assert.True(created.Succeeded, created.Error);
        return created.Value;
    }

    /// <summary>The company the development seed made.</summary>
    protected Task<Guid> DevCompanyIdAsync() =>
        InDbAsync(db => db.Companies.Where(c => c.Name == DevUserSeeder.DefaultOrgName).Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken));

    protected Task<Guid> RoleIdAsync(Guid companyId, string name)
    {
        var normalized = NameInput.Normalize(name);
        return InDbAsync(db => db.CompanyRoles
            .Where(r => r.CompanyId == companyId && r.NormalizedName == normalized)
            .Select(r => r.Id)
            .SingleAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Makes an account with <see cref="MailTestBase.UserPassword"/> and puts it into the company with the named role.</summary>
    protected async Task<Guid> AddMemberAsync(Guid companyId, string email, string role, bool confirmed = true)
    {
        var user = await CreateUserAsync(email, confirmed);
        var roleId = await RoleIdAsync(companyId, role);
        await InDbAsync(async db =>
        {
            db.Memberships.Add(new Membership { UserId = user.Id, CompanyId = companyId, RoleId = roleId, JoinedAt = StorableTime.Now(Clock) });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });
        return user.Id;
    }
}
