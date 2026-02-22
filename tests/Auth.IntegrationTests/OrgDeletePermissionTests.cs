using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

public sealed class OrgDeletePermissionTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public void It_is_a_fourth_built_in_permission_next_to_the_other_three()   // spec 0008 → Company deletion
    {
        Assert.Equal("org:delete", PermissionCatalog.OrgDelete);
        Assert.Equal(["members:manage", "org:delete", "org:manage", "roles:manage"], PermissionCatalog.BuiltIn.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void It_is_in_the_catalog_whether_or_not_the_manifest_lists_it_and_star_expands_to_it()
    {
        var unlisted = new PermissionCatalog(["docs:read"]);
        var listed = new PermissionCatalog(["docs:read", "org:delete"]);

        Assert.Contains("org:delete", unlisted.Permissions);
        Assert.Contains("org:delete", unlisted.Expand(["*"]));
        Assert.Equal(unlisted.Permissions, listed.Permissions);   // listing it changes nothing
        Assert.Contains("org:delete", unlisted.Listed);
    }

    [Fact]
    public void A_role_may_hold_it_by_name_without_star()
    {
        var catalog = new PermissionCatalog(["docs:read"]);

        Assert.True(catalog.Accepts("org:delete"));
        Assert.Equal(["org:delete"], catalog.Expand(["org:delete"]));
    }

    [Fact]
    public async Task The_token_of_an_admin_whose_role_holds_star_carries_it()   // the permissions claim gains org:delete
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        Assert.Contains("org:delete", AccessTokens.Array(session.AccessToken, "permissions"));
    }
}
