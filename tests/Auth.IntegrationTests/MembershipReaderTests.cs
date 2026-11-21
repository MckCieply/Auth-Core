using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0015 → Sign-in space: the reads of a user's memberships and the order that picks the space a sign-in lands in.</summary>
public sealed class MembershipReaderTests : TenancyTestBase
{
    public MembershipReaderTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The private role grants reports:read and reports:approve.
        Factory.WithManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.PrivateBlock);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private DateTimeOffset T0 => StorableTime.Now(Clock);

    private async Task<Guid> NewUserAsync(string email = "someone@acme.test") => (await CreateUserAsync(email, true, member: false)).Id;

    /// <summary>A company made straight in the database with a chosen id and its admin role; the user is not put into it.</summary>
    private Task<Guid> CompanyWithIdAsync(Guid id, string name) =>
        InDbAsync(async db =>
        {
            db.Companies.Add(new Company { Id = id, Name = name, Kind = CompanyKind.Organization, CreatedAt = T0 });
            db.CompanyRoles.Add(new CompanyRole { CompanyId = id, Name = "admin", NormalizedName = NameInput.Normalize("admin"), Permissions = ["*"] });
            await db.SaveChangesAsync(Cancel);
            return id;
        });

    /// <summary>The user's personal space, joined at the given time; returns its id.</summary>
    private Task<Guid> PersonalSpaceAsync(Guid userId, DateTimeOffset joinedAt) =>
        InDbAsync(async db =>
        {
            var id = PersonalSpaces.Add(db, userId, joinedAt);
            await db.SaveChangesAsync(Cancel);
            return id;
        });

    private Task<int> UsedAsync(Guid userId, Guid spaceId, DateTimeOffset at) =>
        InDbAsync(db => db.Memberships.Where(m => m.UserId == userId && m.CompanyId == spaceId).ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedAt, at), Cancel));

    private async Task<TenantContext?> SignInSpaceAsync(Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MembershipReader>().ReadSignInSpaceAsync(userId, Cancel);
    }

    private async Task<TenantContext?> InCompanyAsync(Guid userId, Guid companyId)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MembershipReader>().ReadInCompanyAsync(userId, companyId, Cancel);
    }

    [Fact]
    public async Task The_sign_in_space_is_the_latest_used_first()
    {
        var user = await NewUserAsync();
        var a = await CreateCompanyAsync("A");
        var b = await CreateCompanyAsync("B");
        await AddMembershipAsync(user, a, "admin", T0);
        await AddMembershipAsync(user, b, "admin", T0.AddMinutes(1));
        await UsedAsync(user, a, T0.AddHours(1));
        await UsedAsync(user, b, T0.AddHours(2));

        Assert.Equal(b, (await SignInSpaceAsync(user))?.CompanyId);

        await UsedAsync(user, a, T0.AddHours(3));

        Assert.Equal(a, (await SignInSpaceAsync(user))?.CompanyId);
    }

    [Fact]
    public async Task A_membership_never_used_comes_after_every_used_one()
    {
        var user = await NewUserAsync();
        var a = await CreateCompanyAsync("A");
        var b = await CreateCompanyAsync("B");
        await AddMembershipAsync(user, a, "admin", T0);
        await AddMembershipAsync(user, b, "admin", T0.AddMinutes(1));
        await UsedAsync(user, b, T0.AddHours(1));

        Assert.Equal(b, (await SignInSpaceAsync(user))?.CompanyId);   // A joined earlier, but was never used

        var other = await NewUserAsync("other@acme.test");
        var company = await CreateCompanyAsync("C");
        await AddMembershipAsync(other, company, "admin", T0);   // a company joined earlier and never used
        var personal = await PersonalSpaceAsync(other, T0.AddMinutes(1));
        await UsedAsync(other, personal, T0.AddHours(1));

        var tenant = await SignInSpaceAsync(other);

        Assert.Equal(personal, tenant?.CompanyId);
        Assert.Equal(CompanyKind.Personal, tenant?.Kind);
    }

    [Fact]
    public async Task Without_use_a_company_comes_before_the_personal_space()
    {
        var user = await NewUserAsync();
        await PersonalSpaceAsync(user, T0);
        var company = await CreateCompanyAsync("Acme");
        await AddMembershipAsync(user, company, "admin", T0.AddHours(1));

        var tenant = await SignInSpaceAsync(user);

        Assert.Equal(company, tenant?.CompanyId);
        Assert.Equal(CompanyKind.Organization, tenant?.Kind);
    }

    [Fact]
    public async Task Without_use_the_earlier_joined_company_comes_first()
    {
        var user = await NewUserAsync();
        var late = await CreateCompanyAsync("Late");
        var early = await CreateCompanyAsync("Early");
        await AddMembershipAsync(user, late, "admin", T0.AddHours(2));
        await AddMembershipAsync(user, early, "admin", T0.AddHours(1));

        Assert.Equal(early, (await SignInSpaceAsync(user))?.CompanyId);
    }

    [Fact]
    public async Task With_equal_times_the_lower_id_comes_first()
    {
        // Text order puts the first id first; the order of Guid.ToByteArray() (three little-endian groups) would put the second first.
        // This pins the uuid (text) order against the ToByteArray-order bug. It does not by itself prove the tie-break is present: the
        // primary-key index order of the memberships gives the same row.
        var first = await CompanyWithIdAsync(new Guid("00000001-0000-0000-0000-000000000000"), "First");
        var second = await CompanyWithIdAsync(new Guid("01000000-0000-0000-0000-000000000000"), "Second");
        var user = await NewUserAsync();
        var joined = T0;
        await AddMembershipAsync(user, second, "admin", joined);
        await AddMembershipAsync(user, first, "admin", joined);

        Assert.Equal(first, (await SignInSpaceAsync(user))?.CompanyId);
    }

    [Fact]
    public async Task The_membership_in_a_company_is_that_one_or_none()
    {
        var user = await NewUserAsync();
        var a = await CreateCompanyAsync("A");
        var b = await CreateCompanyAsync("B");
        var elsewhere = await CreateCompanyAsync("Elsewhere");
        await AddMembershipAsync(user, a, "admin", T0);
        await AddMembershipAsync(user, b, "user", T0.AddHours(1));
        var personal = await PersonalSpaceAsync(user, T0.AddHours(2));

        var inA = await InCompanyAsync(user, a);
        var inB = await InCompanyAsync(user, b);
        var inPersonal = await InCompanyAsync(user, personal);

        Assert.NotNull(inA);
        Assert.Equal(a, inA.CompanyId);
        Assert.Equal("admin", inA.RoleName);
        Assert.Equal(T0, inA.JoinedAt);
        Assert.NotNull(inB);
        Assert.Equal(b, inB.CompanyId);
        Assert.Equal("user", inB.RoleName);
        Assert.Equal(T0.AddHours(1), inB.JoinedAt);
        Assert.Null(await InCompanyAsync(user, elsewhere));
        Assert.Equal(CompanyKind.Personal, inPersonal?.Kind);
        Assert.False(inPersonal?.HoldsAll);
        Assert.Equal(["reports:approve", "reports:read"], inPersonal?.Permissions);
    }

    [Fact]
    public async Task The_list_holds_every_membership_and_the_count_matches()
    {
        var user = await NewUserAsync();
        var company = await CreateCompanyAsync("Acme");
        await AddMembershipAsync(user, company, "admin", T0);
        var personal = await PersonalSpaceAsync(user, T0.AddHours(1));
        var none = await NewUserAsync("none@acme.test");
        using var scope = Factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<MembershipReader>();

        var list = await reader.ListAsync(user, Cancel);

        Assert.Equal(new[] { company, personal }.Order(), list.Select(t => t.CompanyId).Order());
        Assert.Equal(2, await reader.CountAsync(user, Cancel));
        Assert.Empty(await reader.ListAsync(none, Cancel));
        Assert.Equal(0, await reader.CountAsync(none, Cancel));
    }

    [Fact]
    public async Task The_context_carries_the_stored_join_time()
    {
        var user = await NewUserAsync();
        var company = await CreateCompanyAsync("Acme");
        var joined = T0.AddTicks(1234560);   // a whole number of microseconds, none of them round
        await AddMembershipAsync(user, company, "admin", joined);

        var tenant = await SignInSpaceAsync(user);

        Assert.Equal(joined, tenant?.JoinedAt);
    }
}
