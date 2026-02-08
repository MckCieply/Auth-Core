using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

public sealed class PermissionCatalogTests
{
    private static readonly PermissionCatalog Catalog = new(["reports:read", "reports:approve", "templates:manage"]);

    [Fact]
    public void Star_expands_to_the_whole_catalog_sorted_without_star()   // criterion 4
    {
        var expanded = Catalog.Expand(["*"]);

        Assert.Equal(
            ["members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            expanded);
        Assert.DoesNotContain("*", expanded);
    }

    [Fact]
    public void Star_among_other_permissions_still_gives_each_one_once()
    {
        Assert.Equal(Catalog.Permissions, Catalog.Expand(["reports:read", "*", "reports:read"]));
    }

    [Fact]
    public void Listed_permissions_are_sorted_ordinally_without_duplicates()
    {
        Assert.Equal(["reports:approve", "reports:read"], Catalog.Expand(["reports:read", "reports:approve", "reports:read"]));
    }

    [Fact]
    public void A_permission_that_left_the_catalog_grants_nothing_and_is_not_shown()   // criterion 21
    {
        Assert.Equal(["reports:read"], Catalog.Expand(["reports:read", "old:thing"]));
        Assert.Equal(["reports:read"], Catalog.Visible(["old:thing", "reports:read"]));
    }

    [Fact]
    public void The_list_of_a_role_keeps_star_as_it_is_and_puts_it_first()
    {
        Assert.Equal(["*", "reports:read"], Catalog.Visible(["reports:read", "*"]));
    }

    [Fact]
    public void Catalog_accepts_star_the_built_in_permissions_and_the_declared_ones_only()
    {
        Assert.True(Catalog.Accepts("*"));
        Assert.True(Catalog.Accepts("members:manage"));
        Assert.True(Catalog.Accepts("reports:read"));
        Assert.False(Catalog.Accepts("reports:write"));
        Assert.False(Catalog.Accepts("REPORTS:READ"));
        Assert.False(Catalog.Accepts(""));
    }

    [Fact]
    public void Listed_has_star_first_then_every_permission()
    {
        Assert.Equal("*", Catalog.Listed[0]);
        Assert.Equal(Catalog.Permissions, Catalog.Listed.Skip(1));
    }
}
