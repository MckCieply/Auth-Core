using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Auth.IntegrationTests;

public sealed class TenancyTablesTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private async Task<(Guid Company, Guid Role)> CompanyWithRoleAsync(string name = "Acme", string role = "admin", string[]? permissions = null)
    {
        var company = new Company { Id = Guid.NewGuid(), Name = name, CreatedAt = Now() };
        var companyRole = new CompanyRole
        {
            CompanyId = company.Id,
            Name = role,
            NormalizedName = role.ToUpperInvariant(),
            Permissions = permissions ?? ["*"],
        };
        await InDbAsync(async db =>
        {
            db.Companies.Add(company);
            db.CompanyRoles.Add(companyRole);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });
        return (company.Id, companyRole.Id);
    }

    private DateTimeOffset Now() => StorableTime.Now(Clock);

    private Task<int> SaveAsync(Action<AuthDbContext> add) =>
        InDbAsync(async db =>
        {
            add(db);
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    [Fact]
    public async Task Company_role_and_membership_are_stored_and_read_back()
    {
        var (company, role) = await CompanyWithRoleAsync(permissions: ["reports:read", "*", "members:manage"]);
        var user = await Factory.SeedUserIdAsync();
        await SaveAsync(db => db.Memberships.Add(new Membership { UserId = user, CompanyId = company, RoleId = role, JoinedAt = Now() }));

        var read = await InDbAsync(db => db.CompanyRoles.AsNoTracking().SingleAsync(r => r.Id == role, TestContext.Current.CancellationToken));

        Assert.Equal(["reports:read", "*", "members:manage"], read.Permissions);   // as stored, in order
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == company, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Role_may_hold_no_permission_at_all()
    {
        var (_, role) = await CompanyWithRoleAsync(permissions: []);

        var read = await InDbAsync(db => db.CompanyRoles.AsNoTracking().SingleAsync(r => r.Id == role, TestContext.Current.CancellationToken));

        Assert.Empty(read.Permissions);
    }

    [Fact]
    public async Task Role_names_are_unique_within_a_company_whatever_their_case_and_free_in_another()
    {
        var (company, _) = await CompanyWithRoleAsync("Acme", "Admin");

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db => db.CompanyRoles.Add(
            new CompanyRole { CompanyId = company, Name = "ADMIN", NormalizedName = "ADMIN", Permissions = [] })));

        await CompanyWithRoleAsync("Globex", "Admin");   // another company may use the name
    }

    [Fact]
    public async Task Tables_allow_a_user_in_two_companies()   // spec 0005 → Concepts: switching companies later is additive
    {
        var (first, firstRole) = await CompanyWithRoleAsync("Acme");
        var (second, secondRole) = await CompanyWithRoleAsync("Globex");
        var user = (await CreateUserAsync("two@example.com", confirmed: true, member: false)).Id;

        await SaveAsync(db =>
        {
            db.Memberships.Add(new Membership { UserId = user, CompanyId = first, RoleId = firstRole, JoinedAt = Now() });
            db.Memberships.Add(new Membership { UserId = user, CompanyId = second, RoleId = secondRole, JoinedAt = Now() });
        });

        Assert.Equal(2, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Membership_cannot_name_a_role_of_another_company()   // criterion 14: no way across companies, down to the schema
    {
        var (first, _) = await CompanyWithRoleAsync("Acme");
        var (_, foreignRole) = await CompanyWithRoleAsync("Globex");
        var user = await Factory.SeedUserIdAsync();

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db => db.Memberships.Add(
            new Membership { UserId = user, CompanyId = first, RoleId = foreignRole, JoinedAt = Now() })));
    }

    [Fact]
    public async Task Invite_cannot_name_a_role_of_another_company()
    {
        var (first, _) = await CompanyWithRoleAsync("Acme");
        var (_, foreignRole) = await CompanyWithRoleAsync("Globex");

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db => db.Invites.Add(NewInvite(first, foreignRole, "a@example.com"))));
    }

    [Fact]
    public async Task Role_in_use_cannot_be_deleted_by_the_database()
    {
        var (company, role) = await CompanyWithRoleAsync();
        var user = await Factory.SeedUserIdAsync();
        await SaveAsync(db => db.Memberships.Add(new Membership { UserId = user, CompanyId = company, RoleId = role, JoinedAt = Now() }));

        var refused = await Assert.ThrowsAsync<PostgresException>(() => InDbAsync(db =>
            db.Database.ExecuteSqlAsync($"""DELETE FROM "CompanyRoles" WHERE "Id" = {role}""", TestContext.Current.CancellationToken)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    private Invite NewInvite(Guid company, Guid role, string email, byte[]? tokenHash = null) => new()
    {
        CompanyId = company,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        RoleId = role,
        InvitedAt = Now(),
        ExpiresAt = Now() + TimeSpan.FromDays(7),
        TokenHash = tokenHash,
    };

    [Fact]
    public async Task Invite_is_unique_per_company_and_address_but_not_across_companies()   // criterion 9
    {
        var (first, firstRole) = await CompanyWithRoleAsync("Acme");
        var (second, secondRole) = await CompanyWithRoleAsync("Globex");
        await SaveAsync(db => db.Invites.Add(NewInvite(first, firstRole, "a@example.com")));

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db => db.Invites.Add(NewInvite(first, firstRole, "a@example.com"))));

        await SaveAsync(db => db.Invites.Add(NewInvite(second, secondRole, "a@example.com")));
    }

    [Fact]
    public async Task Invites_without_a_token_do_not_collide_and_a_token_hash_is_unique()
    {
        var (company, role) = await CompanyWithRoleAsync();
        await SaveAsync(db =>
        {
            db.Invites.Add(NewInvite(company, role, "a@example.com"));
            db.Invites.Add(NewInvite(company, role, "b@example.com"));
        });
        var hash = new byte[32];
        hash[0] = 7;
        await SaveAsync(db => db.Invites.Add(NewInvite(company, role, "c@example.com", hash)));

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db => db.Invites.Add(NewInvite(company, role, "d@example.com", hash))));
    }

    [Fact]
    public async Task Deleting_the_inviter_keeps_the_invitation()
    {
        var (company, role) = await CompanyWithRoleAsync();
        var inviter = await CreateUserAsync("inviter@example.com", confirmed: true);
        var invite = NewInvite(company, role, "a@example.com");
        var withInviter = new Invite
        {
            CompanyId = invite.CompanyId,
            Email = invite.Email,
            NormalizedEmail = invite.NormalizedEmail,
            RoleId = invite.RoleId,
            InvitedBy = inviter.Id,
            InvitedAt = invite.InvitedAt,
            ExpiresAt = invite.ExpiresAt,
        };
        await SaveAsync(db => db.Invites.Add(withInviter));

        await InDbAsync(db => db.Database.ExecuteSqlAsync($"""DELETE FROM "AspNetUsers" WHERE "Id" = {inviter.Id}""", TestContext.Current.CancellationToken));

        var kept = await InDbAsync(db => db.Invites.AsNoTracking().SingleAsync(i => i.Id == withInviter.Id, TestContext.Current.CancellationToken));
        Assert.Null(kept.InvitedBy);
    }

    [Fact]
    public async Task Queue_row_for_an_invitation_keeps_its_invite_and_may_outlive_it()
    {
        var invite = Guid.NewGuid();
        await SaveAsync(db => db.MailRequests.Add(new MailRequest
        {
            Kind = MailKind.Invitation,
            NormalizedEmail = "A@EXAMPLE.COM",
            InviteId = invite,
            RequestedAt = Now(),
            NextAttemptAt = Now(),
        }));

        var row = await InDbAsync(db => db.MailRequests.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));

        Assert.Equal(invite, row.InviteId);   // no foreign key: the invitation need not exist (cancelled) when the row is read
        Assert.Equal(3, (short)row.Kind);
    }
}
