using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Seeding;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0015 → Company API and <c>GET /auth/me</c>: both act on the company of the token's <c>org_id</c>, checked against the database on every
/// call, not on the space a sign-in would land in now. An account that is an admin in two companies gets a token of each; the sign-in space is
/// made to be the other company's, so a call that still read it would answer for the wrong company.
/// </summary>
public sealed class TokenCompanyTests : TenancyTestBase
{
    private const string MePath = "/auth/me";
    private const string OrgPath = "/auth/org";
    private const string MembersPath = "/auth/org/members";
    private const string InvitesPath = "/auth/org/invites";
    private const string RolesPath = "/auth/org/roles";

    public TokenCompanyTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
    }

    private DateTimeOffset Now => StorableTime.Now(Clock);

    /// <summary>The seed user, an admin of the development company (A) and of a second company (B) that a sign-in does not choose first.</summary>
    private async Task<(Guid User, Guid A, Guid B)> SeedAdminInTwoCompaniesAsync()
    {
        var user = await Factory.SeedUserIdAsync();
        var a = await DevCompanyIdAsync();
        var b = await CreateCompanyAsync("Second");
        await AddMembershipAsync(user, b, "admin");
        return (user, a, b);
    }

    private Task<int> SetLastUsedAtAsync(Guid user, Guid company, DateTimeOffset at) =>
        InDbAsync(db => db.Memberships.Where(m => m.UserId == user && m.CompanyId == company)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedAt, at), TestContext.Current.CancellationToken));

    /// <summary>A token of one company: the membership is made the latest used one, as a switch would, and the account signs in.</summary>
    private async Task<string> TokenOfAsync(Guid user, Guid company)
    {
        await SetLastUsedAtAsync(user, company, Now.AddHours(1));
        var session = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        return session.AccessToken;
    }

    /// <summary>
    /// The tokens of A and of B, and B the space a sign-in lands in now: a call that reads the sign-in space instead of the token's company
    /// answers for B, whatever the token says.
    /// </summary>
    private async Task<(string InA, string InB)> TokensAsync(Guid user, Guid a, Guid b)
    {
        var inA = await TokenOfAsync(user, a);
        var inB = await TokenOfAsync(user, b);
        await SetLastUsedAtAsync(user, b, Now.AddDays(1));
        return (inA, inB);
    }

    [Fact]
    public async Task Me_answers_for_the_token_company()   // criterion 23
    {
        var (user, a, b) = await SeedAdminInTwoCompaniesAsync();
        var (inA, inB) = await TokensAsync(user, a, b);

        using var forA = await TenancyApi.Get(Client, MePath, inA);
        using var forB = await TenancyApi.Get(Client, MePath, inB);

        var meA = await TenancyApi.ReadOkAsync(forA);
        var meB = await TenancyApi.ReadOkAsync(forB);
        Assert.Equal(a.ToString(), meA.GetProperty("org_id").GetString());
        Assert.Equal(DevUserSeeder.DefaultOrgName, meA.GetProperty("org_name").GetString());
        Assert.Equal(b.ToString(), meB.GetProperty("org_id").GetString());
        Assert.Equal("Second", meB.GetProperty("org_name").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Each_token_reads_and_changes_only_its_own_company(bool tokenOfA)   // criterion 23
    {
        var (user, a, b) = await SeedAdminInTwoCompaniesAsync();
        var (inA, inB) = await TokensAsync(user, a, b);
        var (token, mine, other) = tokenOfA ? (inA, a, b) : (inB, b, a);
        var workerMine = await AddMemberAsync(mine, "worker-mine@acme.test", "user");
        var workerOther = await AddMemberAsync(other, "worker-other@acme.test", "user");

        // Reading: the company, its members.
        using (var read = await TenancyApi.Get(Client, OrgPath, token))
        {
            Assert.Equal(mine.ToString(), (await TenancyApi.ReadOkAsync(read)).GetProperty("id").GetString());
        }

        using (var members = await TenancyApi.Get(Client, MembersPath, token))
        {
            var emails = (await TenancyApi.ReadOkAsync(members)).GetProperty("members").EnumerateArray()
                .Select(m => m.GetProperty("email").GetString()).Order(StringComparer.Ordinal);
            Assert.Equal(new[] { "worker-mine@acme.test", Factory.SeedEmail }.Order(StringComparer.Ordinal), emails);
        }

        // Renaming: this company only.
        var otherName = await InDbAsync(db => db.Companies.Where(c => c.Id == other).Select(c => c.Name).SingleAsync(TestContext.Current.CancellationToken));
        using (var rename = await TenancyApi.Send(Client, HttpMethod.Patch, OrgPath, token, new { name = "Renamed by token" }))
        {
            await TenancyApi.AssertEmptyAsync(rename, HttpStatusCode.NoContent);
        }

        Assert.Equal("Renamed by token", await InDbAsync(db => db.Companies.Where(c => c.Id == mine).Select(c => c.Name).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(otherName, await InDbAsync(db => db.Companies.Where(c => c.Id == other).Select(c => c.Name).SingleAsync(TestContext.Current.CancellationToken)));

        // An invitation goes to this company.
        var roleOfMine = await RoleIdAsync(mine, "user");
        using (var invite = await TenancyApi.Send(Client, HttpMethod.Post, InvitesPath, token, new { email = "invitee@acme.test", role_id = roleOfMine }))
        {
            await TenancyApi.AssertEmptyAsync(invite, HttpStatusCode.Accepted);
        }

        var invitedTo = await InDbAsync(db => db.Invites.AsNoTracking().Where(i => i.Email == "invitee@acme.test").Select(i => i.CompanyId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([mine], invitedTo);

        // A role created lands in this company.
        using (var created = await TenancyApi.Send(Client, HttpMethod.Post, RolesPath, token, new { name = "Auditors", permissions = Array.Empty<string>() }))
        {
            await TenancyApi.ReadCreatedAsync(created);
        }

        var rolesNamed = await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.Name == "Auditors").Select(r => r.CompanyId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([mine], rolesNamed);

        // A member removed is this company's; the other company's member stays.
        using (var removal = await TenancyApi.Send(Client, HttpMethod.Delete, $"{MembersPath}/{workerMine}", token))
        {
            await TenancyApi.AssertEmptyAsync(removal, HttpStatusCode.NoContent);
        }

        Assert.False(await InDbAsync(db => db.Memberships.AnyAsync(m => m.UserId == workerMine && m.CompanyId == mine, TestContext.Current.CancellationToken)));
        Assert.True(await InDbAsync(db => db.Memberships.AnyAsync(m => m.UserId == workerOther && m.CompanyId == other, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_token_company_left_is_permissions_changed_on_me_and_the_company_api_even_with_another_membership()   // criterion 24
    {
        var (user, a, b) = await SeedAdminInTwoCompaniesAsync();
        var (inA, inB) = await TokensAsync(user, a, b);

        await RemoveMembershipAsync(user, a);

        foreach (var path in new[] { MePath, OrgPath, MembersPath })
        {
            using var response = await TenancyApi.Get(Client, path, inA);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
        }

        using var other = await TenancyApi.Get(Client, MePath, inB);
        Assert.Equal(b.ToString(), (await TenancyApi.ReadOkAsync(other)).GetProperty("org_id").GetString());
    }

    [Fact]
    public async Task A_company_token_whose_permission_was_lost_in_that_company_is_permissions_changed_while_the_other_company_grants_it()
    {
        var (user, a, b) = await SeedAdminInTwoCompaniesAsync();
        var (inA, inB) = await TokensAsync(user, a, b);

        await SetMemberRoleAsync(user, a, "user");   // B still makes the account an admin

        using var inCompanyA = await TenancyApi.Get(Client, MembersPath, inA);
        await TenancyApi.AssertErrorAsync(inCompanyA, HttpStatusCode.Forbidden, "permissions_changed");
        using var inCompanyB = await TenancyApi.Get(Client, MembersPath, inB);
        await TenancyApi.ReadOkAsync(inCompanyB);
    }

    [Fact]
    public async Task The_guard_reads_the_actor_in_the_company_it_locks()
    {
        // What this discriminates: a half-fix that reads the sign-in space (B) and drops the company comparison, where B's admin rights would let
        // the removal through. It does not tell the new guard from the old one (the old one also answers permissions_changed here); that
        // difference is pinned by the member-removal step of Each_token_reads_and_changes_only_its_own_company (the tokenOfA row).
        var (user, a, b) = await SeedAdminInTwoCompaniesAsync();
        var (inA, _) = await TokensAsync(user, a, b);
        var worker = await AddMemberAsync(a, "worker@acme.test", "user");

        // The endpoint's check passes (the account is an admin of A); then the request waits for the lock of A, and while it waits the account is
        // demoted in A only. B, the sign-in space, still makes it an admin: the guard has to look at A.
        await using var held = await HeldLock.CompanyAsync(Factory, a);
        var pending = TenancyApi.Send(Client, HttpMethod.Delete, $"{MembersPath}/{worker}", inA);
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await SetMemberRoleAsync(user, a, "user");
        await held.ReleaseAsync();

        using var response = await pending;
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
        Assert.True(await InDbAsync(db => db.Memberships.AnyAsync(m => m.UserId == worker && m.CompanyId == a, TestContext.Current.CancellationToken)));
    }
}
