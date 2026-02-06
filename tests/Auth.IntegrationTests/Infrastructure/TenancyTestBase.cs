using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Seeding;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
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

    /// <summary>A company with an admin who is logged in: the company, the admin's user id and their access token.</summary>
    protected async Task<(Guid Company, Guid AdminId, string Token)> CompanyWithAdminAsync(string name = "Acme", string email = "boss@acme.test")
    {
        var company = await CreateCompanyAsync(name);
        var admin = await AddMemberAsync(company, email, "admin");
        return (company, admin, (await SessionApi.LoginAsync(Client, email, UserPassword)).AccessToken);
    }

    /// <summary>What the company API would know of the caller: their membership and role as the database has them now.</summary>
    protected async Task<TenantContext> TenantOfAsync(Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MembershipReader>().ReadAsync(userId, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The user belongs to no company.");
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

    /// <summary>Makes an invitation straight in the database, as the API would, with no token yet.</summary>
    protected async Task<Guid> AddInviteAsync(Guid companyId, string email, string role, Guid? invitedBy = null)
    {
        var roleId = await RoleIdAsync(companyId, role);
        using var scope = Factory.Services.CreateScope();
        var normalized = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>().NormalizeEmail(email);
        var now = StorableTime.Now(Clock);
        var invite = new Invite
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            Email = email,
            NormalizedEmail = normalized,
            RoleId = roleId,
            InvitedBy = invitedBy,
            InvitedAt = now,
            ExpiresAt = now + InviteTokens.Lifetime,
        };
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        db.Invites.Add(invite);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return invite.Id;
    }

    /// <summary>Puts the mail of an invitation on the queue, due now, as sending or resending does.</summary>
    protected Task EnqueueInvitationAsync(Guid inviteId, string email) =>
        InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            db.MailRequests.Add(new MailRequest
            {
                Kind = MailKind.Invitation,
                NormalizedEmail = email.ToUpperInvariant(),
                InviteId = inviteId,
                RequestedAt = now,
                NextAttemptAt = now,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

    protected Task<Invite?> InviteAsync(Guid inviteId) =>
        InDbAsync(db => db.Invites.AsNoTracking().SingleOrDefaultAsync(i => i.Id == inviteId, TestContext.Current.CancellationToken));

    /// <summary>Changes what a role holds, straight in the database.</summary>
    protected Task SetRolePermissionsAsync(Guid roleId, params string[] permissions) =>
        InDbAsync(async db =>
        {
            var role = await db.CompanyRoles.SingleAsync(r => r.Id == roleId, TestContext.Current.CancellationToken);
            role.Permissions = permissions;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

    /// <summary>Gives a member another role of their company, straight in the database.</summary>
    protected Task SetMemberRoleAsync(Guid userId, Guid roleId) =>
        InDbAsync(async db =>
        {
            var membership = await db.Memberships.SingleAsync(m => m.UserId == userId, TestContext.Current.CancellationToken);
            membership.RoleId = roleId;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

    /// <summary>Ends a membership, straight in the database.</summary>
    protected Task RemoveMembershipAsync(Guid userId) =>
        InDbAsync(db => db.Memberships.Where(m => m.UserId == userId).ExecuteDeleteAsync(TestContext.Current.CancellationToken));

    /// <summary>Adds a role to a company straight in the database; returns its id.</summary>
    protected Task<Guid> AddRoleAsync(Guid companyId, string name, params string[] permissions) =>
        InDbAsync(async db =>
        {
            var role = new CompanyRole { CompanyId = companyId, Name = name, NormalizedName = NameInput.Normalize(name), Permissions = permissions };
            db.CompanyRoles.Add(role);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return role.Id;
        });

    /// <summary>Makes an account with <see cref="MailTestBase.UserPassword"/> and puts it into the company with the named role.</summary>
    protected async Task<Guid> AddMemberAsync(Guid companyId, string email, string role, bool confirmed = true)
    {
        var user = await CreateUserAsync(email, confirmed, member: false);
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
