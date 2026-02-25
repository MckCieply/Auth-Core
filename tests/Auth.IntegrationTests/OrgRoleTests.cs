using System.Net;
using System.Text;
using System.Text.Json;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class OrgRoleTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string RolesPath = "/auth/org/roles";

    private Task<HttpResponseMessage> CreateAsync(string token, string name, params string[] permissions) =>
        TenancyApi.Send(Client, HttpMethod.Post, RolesPath, token, new { name, permissions });

    private Task<HttpResponseMessage> ReplaceAsync(string token, Guid role, string name, params string[] permissions) =>
        TenancyApi.Send(Client, HttpMethod.Put, $"{RolesPath}/{role}", token, new { name, permissions });

    private Task<HttpResponseMessage> DeleteAsync(string token, Guid role) =>
        TenancyApi.Send(Client, HttpMethod.Delete, $"{RolesPath}/{role}", token);

    private Task<CompanyRole?> RoleAsync(Guid role) =>
        InDbAsync(db => db.CompanyRoles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == role, TestContext.Current.CancellationToken));

    private async Task<JsonElement> ListAsync(string token)
    {
        using var response = await TenancyApi.Get(Client, RolesPath, token);
        return await TenancyApi.ReadOkAsync(response);
    }

    private static string?[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString())];

    /// <summary>A company with a member whose role holds exactly these permissions, logged in.</summary>
    private async Task<(Guid Company, string Token)> CompanyWithCallerAsync(params string[] permissions)
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "caller", permissions);
        await AddMemberAsync(company, "caller@acme.test", "caller");
        return (company, (await SessionApi.LoginAsync(Client, "caller@acme.test", UserPassword)).AccessToken);
    }

    // ---- listing

    [Fact]
    public async Task List_shows_the_roles_sorted_by_name_with_their_permissions_member_counts_and_the_catalog()
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await AddMemberAsync(company, "a@acme.test", "user");
        await AddMemberAsync(company, "b@acme.test", "user");
        await AddRoleAsync(company, "Auditor", "reports:read", "org:manage");

        var list = await ListAsync(token);

        Assert.Equal(["catalog", "roles"], list.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var roles = list.GetProperty("roles").EnumerateArray().ToList();
        Assert.Equal(["Auditor", "admin", "user"], roles.Select(r => r.GetProperty("name").GetString()));   // ordinal
        Assert.Equal(["id", "members", "name", "permissions"], roles[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["org:manage", "reports:read"], Strings(roles[0].GetProperty("permissions")));
        Assert.Equal(["*"], Strings(roles[1].GetProperty("permissions")));   // star is shown as it is
        Assert.Equal([0, 1, 2], roles.Select(r => r.GetProperty("members").GetInt32()));
        Assert.Equal(
            ["*", "members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            Strings(list.GetProperty("catalog")));
    }

    [Fact]
    public async Task List_shows_only_permissions_that_are_still_in_the_catalog()   // criterion 21
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await AddRoleAsync(company, "old", "reports:read", "gone:forever");

        var role = (await ListAsync(token)).GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "old");

        Assert.Equal(["reports:read"], Strings(role.GetProperty("permissions")));
        Assert.Equal(["gone:forever", "reports:read"], (await RoleAsync(role.GetProperty("id").GetGuid()))!.Permissions.Order(StringComparer.Ordinal));   // the database keeps the name
    }

    [Fact]
    public async Task List_is_open_to_members_manage_as_well_as_roles_manage_and_to_nobody_else()   // spec 0005 → Company API
    {
        var (_, rolesToken) = await CompanyWithCallerAsync("roles:manage");
        using (var roles = await TenancyApi.Get(Client, RolesPath, rolesToken))
        {
            Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
        }

        var company = await CreateCompanyAsync("Initech");
        await AddRoleAsync(company, "inviter", "members:manage");
        await AddRoleAsync(company, "nothing");
        await AddMemberAsync(company, "inviter@initech.test", "inviter");
        await AddMemberAsync(company, "nothing@initech.test", "nothing");

        using (var inviter = await TenancyApi.Get(Client, RolesPath, (await SessionApi.LoginAsync(Client, "inviter@initech.test", UserPassword)).AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, inviter.StatusCode);
        }

        using var nothing = await TenancyApi.Get(Client, RolesPath, (await SessionApi.LoginAsync(Client, "nothing@initech.test", UserPassword)).AccessToken);
        await TenancyApi.AssertErrorAsync(nothing, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task List_holds_no_role_of_another_company()   // criterion 14
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        var globex = await CreateCompanyAsync("Globex");
        await AddRoleAsync(globex, "secret", "reports:read");

        var names = (await ListAsync(token)).GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("name").GetString());

        Assert.Equal(["admin", "user"], names);
    }

    [Fact]
    public async Task The_catalog_follows_the_manifest()   // criterion 21
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        Holder.Set(ManifestParser.Parse("permissions: [orders:read]\ndefault_roles:\n  admin: [\"*\"]\n").Manifest!, null);

        var list = await ListAsync(token);

        Assert.Equal(["*", "members:manage", "orders:read", "org:delete", "org:manage", "roles:manage"], Strings(list.GetProperty("catalog")));
    }

    // ---- creating

    [Fact]
    public async Task Admin_creates_a_role_from_permissions_of_the_catalog()   // criterion 18
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        using var response = await CreateAsync(token, "Auditor", "reports:read", "org:manage", "reports:read");

        var role = await TenancyApi.ReadCreatedAsync(response);
        Assert.Equal(["id", "members", "name", "permissions"], role.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Auditor", role.GetProperty("name").GetString());
        Assert.Equal(["org:manage", "reports:read"], Strings(role.GetProperty("permissions")));   // once each, sorted
        Assert.Equal(0, role.GetProperty("members").GetInt32());
        var stored = (await RoleAsync(role.GetProperty("id").GetGuid()))!;
        Assert.Equal(company, stored.CompanyId);
        Assert.Equal("AUDITOR", stored.NormalizedName);
    }

    [Fact]
    public async Task A_role_may_hold_nothing_and_star_stands_alone()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var empty = await CreateAsync(token, "Guest");
        using var star = await CreateAsync(token, "Root", "reports:read", "*");

        Assert.Empty(Strings((await TenancyApi.ReadCreatedAsync(empty)).GetProperty("permissions")));
        Assert.Equal(["*"], Strings((await TenancyApi.ReadCreatedAsync(star)).GetProperty("permissions")));
    }

    [Theory]
    [InlineData("reports:write")]      // not in the catalog
    [InlineData("REPORTS:READ")]       // case matters
    [InlineData("")]
    [InlineData("reports:read ")]
    public async Task A_permission_outside_the_catalog_is_unknown_permission(string permission)   // criterion 18
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await CreateAsync(token, "Auditor", "reports:read", permission);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "unknown_permission");
        Assert.Equal(0, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.Name == "Auditor", TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Padded")]
    [InlineData("Padded ")]
    [InlineData("Ac\tme")]
    [InlineData("admin\u200B")]   // a zero-width space: it would look like admin
    public async Task A_name_that_breaks_the_rules_is_a_400(string name)
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await CreateAsync(token, name, "reports:read");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_name_of_100_characters_is_accepted_and_of_101_is_a_400()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using (var long101 = await CreateAsync(token, new string('a', 101)))
        {
            await TenancyApi.AssertErrorAsync(long101, HttpStatusCode.BadRequest, "invalid_request");
        }

        using var long100 = await CreateAsync(token, new string('a', 100));
        Assert.Equal(HttpStatusCode.Created, long100.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"name":"X"}""")]
    [InlineData("""{"permissions":[]}""")]
    [InlineData("""{"name":"X","permissions":"reports:read"}""")]
    [InlineData("""{"name":"X","permissions":null}""")]
    [InlineData("""{"name":"X","permissions":[1]}""")]
    [InlineData("""{"name":"X","permissions":[null]}""")]
    [InlineData("""{"name":"X","permissions":[["reports:read"]]}""")]
    [InlineData("""{"name":7,"permissions":[]}""")]
    [InlineData("""["X"]""")]
    [InlineData("not json")]
    public async Task A_malformed_body_is_a_400_invalid_request(string body)
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        using var request = TenancyApi.Request(HttpMethod.Post, RolesPath, token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_name_the_company_has_is_taken_whatever_its_case_and_free_in_another_company()   // criterion 18
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        var globex = await CreateCompanyAsync("Globex");
        await AddMemberAsync(globex, "boss@globex.test", "admin");

        foreach (var name in new[] { "admin", "ADMIN", "Admin" })
        {
            using var response = await CreateAsync(token, name, "reports:read");
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "role_name_taken");
        }

        var globexToken = (await SessionApi.LoginAsync(Client, "boss@globex.test", UserPassword)).AccessToken;
        using var elsewhere = await CreateAsync(globexToken, "Auditor");
        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
        using var again = await CreateAsync(globexToken, "AUDITOR");
        await TenancyApi.AssertErrorAsync(again, HttpStatusCode.Conflict, "role_name_taken");
    }

    [Fact]
    public async Task Nobody_creates_a_role_that_holds_a_permission_they_lack()   // criterion 15
    {
        var (_, token) = await CompanyWithCallerAsync("roles:manage", "reports:read");

        using (var tooMuch = await CreateAsync(token, "Wide", "reports:read", "reports:approve"))
        {
            await TenancyApi.AssertErrorAsync(tooMuch, HttpStatusCode.Forbidden, "permission_not_held");
        }

        using (var star = await CreateAsync(token, "Root", "*"))
        {
            await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        }

        using var subset = await CreateAsync(token, "Narrow", "reports:read");
        Assert.Equal(HttpStatusCode.Created, subset.StatusCode);
        using var own = await CreateAsync(token, "Same", "roles:manage", "reports:read");
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
    }

    [Fact]
    public async Task A_caller_that_lists_every_permission_but_not_star_cannot_create_a_role_with_star()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "everything", [.. Holder.Current.Catalog.Permissions]);
        await AddMemberAsync(company, "all@acme.test", "everything");
        var token = (await SessionApi.LoginAsync(Client, "all@acme.test", UserPassword)).AccessToken;

        using var star = await CreateAsync(token, "Root", "*");
        await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        using var every = await CreateAsync(token, "Everything else", [.. Holder.Current.Catalog.Permissions]);
        Assert.Equal(HttpStatusCode.Created, every.StatusCode);
    }

    [Fact]
    public async Task Parallel_creations_of_one_name_leave_one_role()
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        var statuses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var response = await CreateAsync(token, "Auditor", "reports:read");
            return response.StatusCode;
        })));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(1, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == company && r.Name == "Auditor", TestContext.Current.CancellationToken)));
    }

    // ---- replacing

    [Fact]
    public async Task Replace_changes_the_name_and_the_permissions_as_a_whole()   // criterion 18
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");

        using var response = await ReplaceAsync(token, user, "Staff", "templates:manage");

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        var role = (await RoleAsync(user))!;
        Assert.Equal("Staff", role.Name);
        Assert.Equal("STAFF", role.NormalizedName);
        Assert.Equal(["templates:manage"], role.Permissions);   // what it held before is gone
    }

    [Fact]
    public async Task A_role_can_be_renamed_to_another_spelling_of_its_own_name()
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");

        using var response = await ReplaceAsync(token, user, "USER", "reports:read");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("USER", (await RoleAsync(user))!.Name);
    }

    [Fact]
    public async Task A_name_another_role_has_is_taken()   // criterion 18
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        using var response = await ReplaceAsync(token, await RoleIdAsync(company, "user"), "ADMIN", "reports:read");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "role_name_taken");
    }

    [Fact]
    public async Task A_replace_with_a_permission_outside_the_catalog_is_unknown_permission()   // criterion 18
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");

        using var response = await ReplaceAsync(token, user, "user", "reports:write");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "unknown_permission");
        Assert.Equal(["reports:approve", "reports:read"], (await RoleAsync(user))!.Permissions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_edit_drops_the_permissions_that_left_the_catalog()   // spec 0005 → Manifest
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var old = await AddRoleAsync(company, "old", "reports:read", "gone:forever");

        using var response = await ReplaceAsync(token, old, "old", "reports:read");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["reports:read"], (await RoleAsync(old))!.Permissions);
    }

    [Fact]
    public async Task The_edit_of_a_role_reaches_its_members_at_the_next_refresh_and_ends_no_session()   // criterion 12
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await AddMemberAsync(company, "worker@acme.test", "user");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        Assert.Equal(["reports:approve", "reports:read"], AccessTokens.Array(session.AccessToken, "permissions"));

        using (var edit = await ReplaceAsync(token, await RoleIdAsync(company, "user"), "user", "templates:manage"))
        {
            Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        }

        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Assert.Equal(["templates:manage"], AccessTokens.Array(refreshed.AccessToken, "permissions"));
        _ = await SessionApi.RefreshOk(Client, refreshed.RefreshToken);
    }

    [Fact]
    public async Task A_role_of_another_company_or_that_does_not_exist_is_a_404()   // criterion 14
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        var globex = await CreateCompanyAsync("Globex");
        var foreign = await RoleIdAsync(globex, "user");

        foreach (var role in new[] { foreign, Guid.NewGuid() })
        {
            using var replace = await ReplaceAsync(token, role, "Mine", "reports:read");
            await TenancyApi.AssertErrorAsync(replace, HttpStatusCode.NotFound, "not_found");
            using var delete = await DeleteAsync(token, role);
            await TenancyApi.AssertErrorAsync(delete, HttpStatusCode.NotFound, "not_found");
        }

        var untouched = (await RoleAsync(foreign))!;
        Assert.Equal("user", untouched.Name);
        Assert.Equal(["reports:approve", "reports:read"], untouched.Permissions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Nobody_edits_a_role_so_that_it_holds_what_they_lack_and_nobody_without_star_touches_a_star_role()   // criterion 15
    {
        var (company, token) = await CompanyWithCallerAsync("roles:manage", "reports:read");
        var starter = await AddRoleAsync(company, "starter", "reports:read");

        using (var tooMuch = await ReplaceAsync(token, starter, "starter", "reports:read", "reports:approve"))
        {
            await TenancyApi.AssertErrorAsync(tooMuch, HttpStatusCode.Forbidden, "permission_not_held");
        }

        using (var star = await ReplaceAsync(token, starter, "starter", "*"))
        {
            await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        }

        // The admin role holds star: even a rename leaves it holding star, which this caller does not.
        using (var rename = await ReplaceAsync(token, await RoleIdAsync(company, "admin"), "Boss", "*"))
        {
            await TenancyApi.AssertErrorAsync(rename, HttpStatusCode.Forbidden, "permission_not_held");
        }

        using (var shrink = await ReplaceAsync(token, starter, "starter", "reports:read"))
        {
            Assert.Equal(HttpStatusCode.NoContent, shrink.StatusCode);   // as much as the caller holds is fine
        }

        Assert.Equal(["*"], (await RoleAsync(await RoleIdAsync(company, "admin")))!.Permissions);
    }

    [Fact]
    public async Task Nobody_edits_or_deletes_a_role_that_holds_more_than_they_do_even_to_shrink_it()   // criterion 15
    {
        var (company, token) = await CompanyWithCallerAsync("roles:manage", "reports:read");
        var user = await RoleIdAsync(company, "user");   // reports:approve and reports:read: one more than the caller holds
        var admin = await RoleIdAsync(company, "admin");   // star

        foreach (var role in new[] { user, admin })
        {
            using var shrink = await ReplaceAsync(token, role, "Renamed", "reports:read");
            await TenancyApi.AssertErrorAsync(shrink, HttpStatusCode.Forbidden, "permission_not_held");
            using var empty = await ReplaceAsync(token, role, "Renamed");
            await TenancyApi.AssertErrorAsync(empty, HttpStatusCode.Forbidden, "permission_not_held");
            using var delete = await DeleteAsync(token, role);
            await TenancyApi.AssertErrorAsync(delete, HttpStatusCode.Forbidden, "permission_not_held");
        }

        var untouched = (await RoleAsync(user))!;
        Assert.Equal("user", untouched.Name);
        Assert.Equal(["reports:approve", "reports:read"], untouched.Permissions.Order(StringComparer.Ordinal));
        Assert.NotNull(await RoleAsync(admin));
    }

    [Fact]
    public async Task A_caller_edits_and_deletes_roles_that_hold_what_they_hold_or_less()   // criterion 15
    {
        var (company, token) = await CompanyWithCallerAsync("roles:manage", "reports:read");
        var equal = await AddRoleAsync(company, "equal", "roles:manage", "reports:read");
        var fewer = await AddRoleAsync(company, "fewer", "reports:read");
        var nothing = await AddRoleAsync(company, "nothing");
        var gone = await AddRoleAsync(company, "gone", "reports:read", "left:the-catalog");   // what left the catalog grants nothing

        using (var edit = await ReplaceAsync(token, equal, "equal", "reports:read"))
        {
            Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        }

        using (var other = await ReplaceAsync(token, fewer, "fewer", "roles:manage"))
        {
            Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
        }

        using (var delete = await DeleteAsync(token, nothing))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        using (var clean = await DeleteAsync(token, gone))
        {
            Assert.Equal(HttpStatusCode.NoContent, clean.StatusCode);
        }

        Assert.Null(await RoleAsync(nothing));
        Assert.Equal(["roles:manage"], (await RoleAsync(fewer))!.Permissions);
    }

    [Fact]
    public async Task A_holder_of_star_edits_and_deletes_a_role_with_star_and_a_list_of_everything_is_not_star()   // criterion 15
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var root = await AddRoleAsync(company, "root", "*");
        var other = await AddRoleAsync(company, "other", "*");

        using (var rename = await ReplaceAsync(token, root, "Root", "*"))
        {
            Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        }

        using (var delete = await DeleteAsync(token, other))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        var everything = await CreateCompanyAsync("Globex");
        await AddRoleAsync(everything, "caller", "members:manage", "roles:manage", "org:manage", "org:delete", "reports:read", "reports:approve", "templates:manage");
        await AddMemberAsync(everything, "caller@globex.test", "caller");
        var starRole = await AddRoleAsync(everything, "root", "*");
        var listed = (await SessionApi.LoginAsync(Client, "caller@globex.test", UserPassword)).AccessToken;

        using var edit = await ReplaceAsync(listed, starRole, "root", "reports:read");
        await TenancyApi.AssertErrorAsync(edit, HttpStatusCode.Forbidden, "permission_not_held");
        using var remove = await DeleteAsync(listed, starRole);
        await TenancyApi.AssertErrorAsync(remove, HttpStatusCode.Forbidden, "permission_not_held");
    }

    [Fact]
    public async Task An_unknown_permission_is_answered_before_a_role_that_does_not_exist()   // spec 0005 → the order of checks
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await ReplaceAsync(token, Guid.NewGuid(), "Mine", "reports:write");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "unknown_permission");   // not 404
    }

    [Fact]
    public async Task Missing_permission_is_answered_before_a_bad_body_on_roles_too()   // spec 0005 → the order of checks
    {
        var company = await CreateCompanyAsync("Acme");
        await AddMemberAsync(company, "worker@acme.test", "user");
        var token = (await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword)).AccessToken;

        using var response = await CreateAsync(token, "", "reports:write");   // an invalid name and an unknown permission

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");   // not 400
    }

    [Fact]
    public async Task An_edit_that_would_leave_the_company_without_a_manager_is_refused()   // criterion 16
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var adminRole = await RoleIdAsync(company, "admin");

        // The only manager is the caller themselves, through star: dropping star would leave nobody who manages members.
        using (var drop = await ReplaceAsync(token, adminRole, "admin", "roles:manage", "org:manage"))
        {
            await TenancyApi.AssertErrorAsync(drop, HttpStatusCode.Conflict, "last_manager");
        }

        Assert.Equal(["*"], (await RoleAsync(adminRole))!.Permissions);
        Assert.NotNull(await InDbAsync(db => db.Memberships.SingleOrDefaultAsync(m => m.UserId == admin, TestContext.Current.CancellationToken)));

        // Keeping members:manage is fine.
        using var keep = await ReplaceAsync(token, adminRole, "admin", "members:manage", "roles:manage");
        Assert.Equal(HttpStatusCode.NoContent, keep.StatusCode);
    }

    [Fact]
    public async Task An_edit_that_takes_members_manage_from_a_role_is_fine_while_another_manager_is_left()   // criterion 16
    {
        var company = await CreateCompanyAsync("Acme");
        var shared = await AddRoleAsync(company, "shared", "members:manage", "roles:manage");
        await AddMemberAsync(company, "a@acme.test", "shared");
        await AddMemberAsync(company, "b@acme.test", "admin");
        var token = (await SessionApi.LoginAsync(Client, "a@acme.test", UserPassword)).AccessToken;

        using var response = await ReplaceAsync(token, shared, "shared", "roles:manage");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);   // b still manages members, through star
    }

    // ---- deleting

    [Fact]
    public async Task A_role_nobody_holds_is_deleted()   // criterion 18
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        using var created = await CreateAsync(token, "Temporary", "reports:read");
        var id = (await TenancyApi.ReadCreatedAsync(created)).GetProperty("id").GetGuid();

        using var response = await DeleteAsync(token, id);

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Null(await RoleAsync(id));
    }

    [Fact]
    public async Task A_role_held_by_a_member_cannot_be_deleted()   // criterion 18
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");
        await AddMemberAsync(company, "worker@acme.test", "user");

        using var response = await DeleteAsync(token, user);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "role_in_use");
        Assert.NotNull(await RoleAsync(user));
    }

    [Fact]
    public async Task A_role_of_a_pending_invitation_cannot_be_deleted_and_of_an_expired_one_can()   // criterion 18
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        var role = await AddRoleAsync(company, "Temporary", "reports:read");
        var invite = await AddInviteAsync(company, "worker@acme.test", "Temporary");
        var token = (await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword)).AccessToken;

        using (var pending = await DeleteAsync(token, role))
        {
            await TenancyApi.AssertErrorAsync(pending, HttpStatusCode.Conflict, "role_in_use");
        }

        Clock.Advance(InviteTokens.Lifetime);
        var fresh = (await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword)).AccessToken;
        using var expired = await DeleteAsync(fresh, role);

        Assert.Equal(HttpStatusCode.NoContent, expired.StatusCode);
        Assert.Null(await InviteAsync(invite));   // it went with the role
    }

    // ---- who may

    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "")]
    [InlineData("PUT", "/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/11111111-1111-1111-1111-111111111111")]
    public async Task Every_endpoint_needs_a_token_and_the_permission_checked_against_the_database(string method, string suffix)   // criterion 14
    {
        var company = await CreateCompanyAsync("Acme");
        var worker = await AddMemberAsync(company, "worker@acme.test", "admin");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        var path = RolesPath + suffix;
        object? body = method is "POST" or "PUT" ? new { name = "X", permissions = Array.Empty<string>() } : null;

        using (var anonymous = await TenancyApi.Send(Client, new HttpMethod(method), path, null, body))
        {
            await TenancyApi.AssertUnauthorizedAsync(anonymous);
        }

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "user"));
        using (var stale = await TenancyApi.Send(Client, new HttpMethod(method), path, session.AccessToken, body))
        {
            await TenancyApi.AssertErrorAsync(stale, HttpStatusCode.Forbidden, "permissions_changed");
        }

        var fresh = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using var plain = await TenancyApi.Send(Client, new HttpMethod(method), path, fresh.AccessToken, body);
        await TenancyApi.AssertErrorAsync(plain, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_member_with_members_manage_but_not_roles_manage_cannot_change_roles()
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "inviter", "members:manage");
        await AddMemberAsync(company, "inviter@acme.test", "inviter");
        var token = (await SessionApi.LoginAsync(Client, "inviter@acme.test", UserPassword)).AccessToken;

        using var response = await CreateAsync(token, "Mine");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("{0}%20")]     // the id of a real role with white space around it is still not a UUID
    [InlineData("%20{0}")]
    [InlineData("{0}%09")]
    public async Task A_path_that_is_not_a_uuid_is_a_400(string path)
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var id = string.Format(System.Globalization.CultureInfo.InvariantCulture, path, await RoleIdAsync(company, "user"));

        using var replace = await TenancyApi.Send(Client, HttpMethod.Put, $"{RolesPath}/{id}", token, new { name = "X", permissions = Array.Empty<string>() });
        using var delete = await TenancyApi.Send(Client, HttpMethod.Delete, $"{RolesPath}/{id}", token);

        await TenancyApi.AssertErrorAsync(replace, HttpStatusCode.BadRequest, "invalid_request");
        await TenancyApi.AssertErrorAsync(delete, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal("user", (await RoleAsync(await RoleIdAsync(company, "user")))!.Name);
    }

    // ---- the lock

    [Fact]
    public async Task A_caller_demoted_before_the_lock_cannot_create_edit_or_delete_a_role()   // safety rule 2: the caller is read again under the lock
    {
        var company = await CreateCompanyAsync("Acme");
        var boss = await AddMemberAsync(company, "boss@acme.test", "admin");
        var spare = await AddRoleAsync(company, "spare", "reports:read");
        var stale = Actor.Of(await TenantOfAsync(boss));
        await SetMemberRoleAsync(boss, await RoleIdAsync(company, "user"));   // demoted after the endpoint read them

        var created = await InScopeAsync(sp => sp.GetRequiredService<RoleService>()
            .CreateAsync(stale, company, "Auditor", ["reports:read"], TestContext.Current.CancellationToken));
        var updated = await InScopeAsync(sp => sp.GetRequiredService<RoleService>()
            .UpdateAsync(stale, company, spare, "Spare", ["reports:approve"], TestContext.Current.CancellationToken));
        var deleted = await InScopeAsync(sp => sp.GetRequiredService<RoleService>()
            .DeleteAsync(stale, company, spare, TestContext.Current.CancellationToken));

        Assert.Equal("permissions_changed", created.Error);
        Assert.Equal("permissions_changed", updated.Error);
        Assert.Equal("permissions_changed", deleted.Error);
        var role = Assert.IsType<CompanyRole>(await RoleAsync(spare));
        Assert.Equal("spare", role.Name);
        Assert.Equal(["reports:read"], role.Permissions);
        Assert.Equal(0, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.Name == "Auditor", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Rule_1_on_roles_is_judged_on_the_caller_as_read_again_under_the_lock()
    {
        var company = await CreateCompanyAsync("Acme");
        var keeper = await AddRoleAsync(company, "keeper", "roles:manage", "reports:read", "reports:approve");
        var caller = await AddMemberAsync(company, "keeper@acme.test", "keeper");
        var stale = Actor.Of(await TenantOfAsync(caller));
        await SetRolePermissionsAsync(keeper, "roles:manage", "reports:read");   // still manages roles, no longer approves

        var created = await InScopeAsync(sp => sp.GetRequiredService<RoleService>()
            .CreateAsync(stale, company, "Approver", ["reports:approve"], TestContext.Current.CancellationToken));

        Assert.Equal("permission_not_held", created.Error);
        Assert.Equal(0, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.Name == "Approver", TestContext.Current.CancellationToken)));
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }
}
