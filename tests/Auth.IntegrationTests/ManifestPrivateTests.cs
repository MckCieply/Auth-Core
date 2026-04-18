using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Manifest: the block <c>private</c>.</summary>
public sealed class ManifestPrivateTests
{
    private const string Roles = """
        permissions: [docs:read, docs:write]
        default_roles:
          admin: ["*"]
          user: [docs:read]
        """;

    private static Manifest Parsed(string yaml)
    {
        var result = ManifestParser.Parse(yaml);
        Assert.Null(result.Error);
        return Assert.IsType<Manifest>(result.Manifest);
    }

    private static string Invalid(string yaml)
    {
        var result = ManifestParser.Parse(yaml);
        Assert.Null(result.Manifest);
        return Assert.IsType<string>(result.Error);
    }

    [Fact]
    public void The_block_turns_private_sign_up_on_and_lists_its_permissions_sorted_without_duplicates()   // criterion 1
    {
        var manifest = Parsed(Roles + "\nprivate:\n  signup: self-service\n  permissions: [docs:write, docs:read, docs:write]\n");

        Assert.True(manifest.PrivateSignup);
        Assert.Equal(["docs:read", "docs:write"], manifest.PrivatePermissions);
        Assert.Equal(SignupMode.InviteOnly, manifest.Signup);   // the two switches are independent
    }

    [Theory]
    [InlineData("off")]
    [InlineData("invite-only")]
    [InlineData("Self-Service")]
    [InlineData("yes")]
    public void Any_other_value_of_the_switch_leaves_it_off_and_the_manifest_valid(string value)   // criterion 1
    {
        var manifest = Parsed(Roles + "\nprivate:\n  signup: " + value + "\n  permissions: [docs:read]\n");

        Assert.False(manifest.PrivateSignup);
        Assert.Equal(["docs:read"], manifest.PrivatePermissions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\nprivate:\n")]
    [InlineData("\nprivate: ~\n")]
    [InlineData("\nprivate: {}\n")]
    [InlineData("\nprivate:\n  signup:\n")]
    [InlineData("\nprivate:\n  permissions:\n")]
    public void No_block_an_empty_block_or_a_key_without_a_value_is_off_with_no_permissions(string block)   // criterion 1
    {
        var manifest = Parsed(Roles + block);

        Assert.False(manifest.PrivateSignup);
        Assert.Empty(manifest.PrivatePermissions);
    }

    [Theory]
    [InlineData("members:manage")]
    [InlineData("roles:manage")]
    [InlineData("org:manage")]
    [InlineData("org:delete")]
    [InlineData("\"*\"")]
    [InlineData("docs:delete")]
    [InlineData("~")]
    public void A_built_in_permission_the_star_or_one_outside_the_catalog_makes_the_manifest_invalid(string permission)   // criterion 1
    {
        var error = Invalid(Roles + "\nprivate:\n  signup: self-service\n  permissions: [docs:read, " + permission + "]\n");

        Assert.Contains("private.permissions", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\nprivate:\n  signup: self-service\n  extra: 1\n")]
    [InlineData("\nprivate: yes\n")]
    [InlineData("\nprivate: [docs:read]\n")]
    [InlineData("\nprivate:\n  signup: self-service\n  signup: off\n")]
    public void Another_key_or_a_block_that_is_not_a_map_makes_the_manifest_invalid(string block)   // criterion 1
    {
        _ = Invalid(Roles + block);
    }

    [Fact]
    public void More_than_500_permissions_make_the_manifest_invalid()
    {
        var many = string.Join(", ", Enumerable.Range(0, 501).Select(_ => "docs:read"));

        var error = Invalid(Roles + "\nprivate:\n  permissions: [" + many + "]\n");

        Assert.Contains("500", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stored_form_keeps_the_block()   // criterion 1
    {
        var manifest = new Manifest(["docs:read"], [new DefaultRole("admin", ["*"])], SignupMode.InviteOnly, privateSignup: true, privatePermissions: ["docs:read"]);

        var back = Manifest.FromJson(manifest.ToJson());

        Assert.NotNull(back);
        Assert.True(back.PrivateSignup);
        Assert.Equal(["docs:read"], back.PrivatePermissions);
    }

    [Fact]
    public void A_manifest_stored_by_slice_0009_reads_as_off_with_no_permissions()   // criterion 1
    {
        const string stored = """{"permissions":["docs:read"],"default_roles":[{"name":"admin","permissions":["*"]}],"signup":"self-service"}""";

        var manifest = Manifest.FromJson(stored);

        Assert.NotNull(manifest);
        Assert.False(manifest.PrivateSignup);
        Assert.Empty(manifest.PrivatePermissions);
        Assert.Equal(SignupMode.SelfService, manifest.Signup);
    }

    [Fact]
    public void A_manifest_without_the_block_is_stored_as_before()   // compatibility: the stored form of slice 0009 does not change
    {
        var manifest = new Manifest(["docs:read"], [new DefaultRole("admin", ["*"])]);

        Assert.Equal(
            """{"permissions":["docs:read"],"default_roles":[{"name":"admin","permissions":["*"]}],"signup":"invite-only"}""",
            manifest.ToJson());
    }

    [Fact]
    public void Exactly_500_permissions_are_accepted()
    {
        var many = string.Join(", ", Enumerable.Repeat("docs:read", 500));

        var manifest = Parsed(Roles + "\nprivate:\n  permissions: [" + many + "]\n");

        Assert.Equal(["docs:read"], manifest.PrivatePermissions);
    }

    [Fact]
    public void A_block_with_the_switch_off_and_permissions_is_stored_and_read_back_as_it_is()
    {
        var manifest = new Manifest(["docs:read"], [new DefaultRole("admin", ["*"])], SignupMode.InviteOnly, privateSignup: false, privatePermissions: ["docs:read"]);

        var json = manifest.ToJson();
        var back = Manifest.FromJson(json);

        Assert.Contains("\"private\":{\"signup\":\"off\",\"permissions\":[\"docs:read\"]}", json, StringComparison.Ordinal);
        Assert.NotNull(back);
        Assert.False(back.PrivateSignup);
        Assert.Equal(["docs:read"], back.PrivatePermissions);
    }
}
