using System.Net;
using System.Text.Json;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0016 → <c>POST /auth/orgs</c>: a signed-in account creates another company and becomes its first admin.</summary>
public sealed class OrgCreateTests : AuditTestBase
{
    private static readonly string[] EntryMembers = ["current", "id", "kind", "name", "role"];

    public OrgCreateTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithOrgCreation();
    }

    private void SetManifest(string yaml) => Holder.Set(ManifestParser.Parse(yaml).Manifest!, null);

    private async Task<JsonElement> ListAsync(string token)
    {
        using var response = await TenancyApi.Get(Client, "/auth/orgs", token);
        return await TenancyApi.ReadOkAsync(response);
    }

    private static JsonElement EntryOf(JsonElement list, Guid id) =>
        list.GetProperty("orgs").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task A_signed_in_account_creates_a_company_and_gets_201_with_the_entry()   // criterion 3
    {
        var (acme, admin, _) = await CompanyWithAdminAsync();
        var session = await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword);

        using var response = await TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Beta" });

        var body = await TenancyApi.ReadCreatedAsync(response);
        Assert.Equal(EntryMembers, body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var id = body.GetProperty("id").GetGuid();
        Assert.NotEqual(acme, id);
        Assert.Equal("Beta", body.GetProperty("name").GetString());
        Assert.Equal("organization", body.GetProperty("kind").GetString());
        Assert.False(body.GetProperty("current").GetBoolean());
        Assert.Equal("admin", body.GetProperty("role").GetProperty("name").GetString());
        Assert.Equal(await RoleIdAsync(id, "admin"), body.GetProperty("role").GetProperty("id").GetGuid());
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        Assert.Equal(2, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == id, TestContext.Current.CancellationToken)));
        var membership = await InDbAsync(db => db.Memberships.AsNoTracking().SingleAsync(m => m.UserId == admin && m.CompanyId == id, TestContext.Current.CancellationToken));
        Assert.Equal(0, membership.JoinedAt.Ticks % 10);   // microseconds, as the database keeps them
        Assert.Null(await LastUsedAtAsync(admin, id));

        // The session did not change: the old token still works in its own company and the refresh cookie keeps it.
        using var own = await TenancyApi.Get(Client, "/auth/org", session.AccessToken);
        Assert.Equal("Acme", (await TenancyApi.ReadOkAsync(own)).GetProperty("name").GetString());
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Assert.Equal(acme.ToString(), AccessTokens.Text(refreshed.AccessToken, "org_id"));
    }

    [Fact]
    public async Task The_new_company_is_listed_and_can_be_switched_to()   // criterion 4
    {
        await CompanyWithAdminAsync();
        var session = await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword);
        using var created = await TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Beta" });
        var id = (await TenancyApi.ReadCreatedAsync(created)).GetProperty("id").GetGuid();

        Assert.False(EntryOf(await ListAsync(session.AccessToken), id).GetProperty("current").GetBoolean());

        var switched = await SessionApi.SwitchOk(Client, session.RefreshToken, id);
        Assert.Equal(id.ToString(), AccessTokens.Text(switched.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(switched.AccessToken, "roles"));
        using var members = await TenancyApi.Get(Client, "/auth/org/members", switched.AccessToken);
        var list = (await TenancyApi.ReadOkAsync(members)).GetProperty("members").EnumerateArray().ToList();
        Assert.Equal("boss@acme.test", Assert.Single(list).GetProperty("email").GetString());
    }

    [Fact]
    public async Task A_token_whose_company_membership_was_removed_still_creates()   // criterion 8
    {
        var (acme, admin, _) = await CompanyWithAdminAsync();
        var other = await CreateCompanyAsync("Other");
        await AddMembershipAsync(admin, other, "user");
        var session = await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword);
        await RemoveMembershipAsync(admin, acme);

        using var response = await TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Beta" });

        _ = await TenancyApi.ReadCreatedAsync(response);
        Assert.Equal(2, await CompanyCountOfAsync(admin));
    }

    [Fact]
    public async Task A_private_account_creates_a_company_and_keeps_its_personal_space()   // criterion 11
    {
        var (user, personal) = await CreatePrivateAccountAsync("solo@example.test");
        var session = await SessionApi.LoginAsync(Client, "solo@example.test", UserPassword);

        using var response = await TenancyApi.CreateOrg(Client, session.AccessToken, new { name = "Solo Ltd" });

        var id = (await TenancyApi.ReadCreatedAsync(response)).GetProperty("id").GetGuid();
        var list = await ListAsync(session.AccessToken);
        Assert.Equal("personal", EntryOf(list, personal).GetProperty("kind").GetString());
        Assert.Equal("organization", EntryOf(list, id).GetProperty("kind").GetString());
        Assert.Equal(1, await CompanyCountOfAsync(user));
    }

    [Fact]
    public async Task org_created_is_written_in_the_creations_transaction()   // criterion 12
    {
        var (_, admin, token) = await CompanyWithAdminAsync();
        var before = (await AuditAsync(AuditKinds.OrgCreated)).Count;

        using var response = await SendFromCallerAsync(HttpMethod.Post, "/auth/orgs", token, new { name = "Beta" });

        var id = (await TenancyApi.ReadCreatedAsync(response)).GetProperty("id").GetGuid();
        var rows = await AuditAsync(AuditKinds.OrgCreated);
        Assert.Equal(before + 1, rows.Count);
        var row = rows.Single(r => r.OrgId == id);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(admin, row.SubjectUserId);
        Assert.Equal("boss@acme.test", row.SubjectEmail);
        Assert.Equal("Beta", row.OrgName);
        Assert.Equal(CallerAddress, row.ClientIp);
        using var details = JsonDocument.Parse(row.Details!);
        Assert.Equal(["via"], details.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("api", details.RootElement.GetProperty("via").GetString());
        Assert.Equal(29, AuditKinds.All.Count);   // no new kind
    }

    [Fact]
    public async Task A_failed_save_writes_no_company_and_no_row()   // criterion 12
    {
        var (_, admin, token) = await CompanyWithAdminAsync();
        var companies = await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken));
        var roles = await InDbAsync(db => db.CompanyRoles.CountAsync(TestContext.Current.CancellationToken));
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var response = await TenancyApi.CreateOrg(Client, token, new { name = "Beta" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(companies, await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(roles, await InDbAsync(db => db.CompanyRoles.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CompanyCountOfAsync(admin));
    }

    [Fact]
    public async Task A_manifest_change_reaches_creation_at_once_and_a_company_made_stays_when_it_is_turned_off()   // Review Focus 2
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        var session = await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword);

        SetManifest(AuthAppFactory.DefaultManifest);
        using var off = await TenancyApi.CreateOrg(Client, token, new { name = "Beta" });
        await TenancyApi.AssertErrorAsync(off, HttpStatusCode.Forbidden, "org_creation_disabled");

        SetManifest(AuthAppFactory.OrgCreationManifest);
        using var on = await TenancyApi.CreateOrg(Client, token, new { name = "Beta" });
        var id = (await TenancyApi.ReadCreatedAsync(on)).GetProperty("id").GetGuid();

        SetManifest(AuthAppFactory.DefaultManifest);
        using var offAgain = await TenancyApi.CreateOrg(Client, token, new { name = "Gamma" });
        await TenancyApi.AssertErrorAsync(offAgain, HttpStatusCode.Forbidden, "org_creation_disabled");

        Assert.Equal("Beta", EntryOf(await ListAsync(token), id).GetProperty("name").GetString());
        var switched = await SessionApi.SwitchOk(Client, session.RefreshToken, id);
        Assert.Equal(id.ToString(), AccessTokens.Text(switched.AccessToken, "org_id"));
    }

    [Fact]
    public async Task A_name_of_100_characters_a_non_ascii_name_and_a_name_in_use_are_created_as_sent()   // Review Focus 3
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        var names = new[] { new string('a', 100), "Zak" + (char)0x0142 + (char)0x0105 + "d " + (char)0x4E2D, "Acme" };

        var ids = new List<Guid>();
        foreach (var name in names)
        {
            using var response = await TenancyApi.CreateOrg(Client, token, new { name });
            var body = await TenancyApi.ReadCreatedAsync(response);
            Assert.Equal(name, body.GetProperty("name").GetString());
            ids.Add(body.GetProperty("id").GetGuid());
        }

        var list = await ListAsync(token);
        for (var i = 0; i < names.Length; i++)
        {
            Assert.Equal(names[i], EntryOf(list, ids[i]).GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task The_creator_gets_the_first_role_that_holds_all_else_the_first_that_manages_members()   // Review Focus 6
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        SetManifest("permissions: [reports:read]\ndefault_roles:\n  helper: [members:manage]\n  worker: [reports:read]\norg_creation: self-service\n");
        using var first = await TenancyApi.CreateOrg(Client, token, new { name = "Helpers" });
        var helpers = await TenancyApi.ReadCreatedAsync(first);
        Assert.Equal("helper", helpers.GetProperty("role").GetProperty("name").GetString());

        SetManifest("permissions: [reports:read]\ndefault_roles:\n  helper: [members:manage]\n  owner: [\"*\"]\n  worker: [reports:read]\norg_creation: self-service\n");
        using var second = await TenancyApi.CreateOrg(Client, token, new { name = "Owners" });
        var owners = await TenancyApi.ReadCreatedAsync(second);
        Assert.Equal("owner", owners.GetProperty("role").GetProperty("name").GetString());
        Assert.Equal(await RoleIdAsync(owners.GetProperty("id").GetGuid(), "owner"), owners.GetProperty("role").GetProperty("id").GetGuid());
    }
}
