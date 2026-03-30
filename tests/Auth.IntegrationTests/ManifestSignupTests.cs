using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

public sealed class ManifestSignupTests
{
    private const string Roles = """
        permissions: [reports:read]
        default_roles:
          admin: ["*"]
          user: [reports:read]
        """;

    private static Manifest Parsed(string yaml)
    {
        var result = ManifestParser.Parse(yaml);
        Assert.Null(result.Error);
        return Assert.IsType<Manifest>(result.Manifest);
    }

    [Fact]
    public void A_manifest_without_the_key_is_invite_only()   // criteria 1, 16
    {
        Assert.Equal(SignupMode.InviteOnly, Parsed(Roles).Signup);
    }

    [Theory]
    [InlineData("invite-only", SignupMode.InviteOnly)]
    [InlineData("self-service", SignupMode.SelfService)]
    public void The_key_accepts_invite_only_and_self_service(string value, SignupMode expected)   // criterion 16
    {
        Assert.Equal(expected, Parsed(Roles + "\nsignup: " + value + "\n").Signup);
    }

    [Theory]
    [InlineData("signup:")]
    [InlineData("signup: ~")]
    public void An_empty_value_counts_as_absent(string line)   // Decision of the plan: like `permissions:` with no value
    {
        Assert.Equal(SignupMode.InviteOnly, Parsed(Roles + "\n" + line + "\n").Signup);
    }

    [Theory]
    [InlineData("sometimes")]
    [InlineData("Self-Service")]
    [InlineData("INVITE-ONLY")]
    [InlineData("self_service")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("''")]
    [InlineData("'self-service '")]
    public void Any_other_scalar_makes_the_manifest_invalid_and_names_the_key(string value)   // criterion 16
    {
        var result = ManifestParser.Parse(Roles + "\nsignup: " + value + "\n");

        Assert.Null(result.Manifest);
        Assert.Contains("signup", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[self-service]")]
    [InlineData("{mode: self-service}")]
    public void A_list_or_a_map_makes_the_manifest_invalid(string value)   // criterion 16
    {
        var result = ManifestParser.Parse(Roles + "\nsignup: " + value + "\n");

        Assert.Null(result.Manifest);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void The_key_twice_is_a_duplicate_key()
    {
        var result = ManifestParser.Parse(Roles + "\nsignup: invite-only\nsignup: self-service\n");

        Assert.Null(result.Manifest);
    }

    [Theory]
    [InlineData(SignupMode.InviteOnly)]
    [InlineData(SignupMode.SelfService)]
    public void The_mode_is_kept_by_the_stored_form(SignupMode mode)   // criterion 16
    {
        var manifest = new Manifest(["reports:read"], [new DefaultRole("admin", ["*"])], mode);

        var back = Manifest.FromJson(manifest.ToJson());

        Assert.NotNull(back);
        Assert.Equal(mode, back.Signup);
        Assert.Contains($"\"signup\":\"{SignupModes.NameOf(mode)}\"", manifest.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_stored_by_v0_1_0_reads_as_invite_only()   // criterion 16: the stored JSON had no such member
    {
        const string stored = """{"permissions":["reports:read"],"default_roles":[{"name":"admin","permissions":["*"]},{"name":"user","permissions":["reports:read"]}]}""";

        var manifest = Manifest.FromJson(stored);

        Assert.NotNull(manifest);
        Assert.Equal(SignupMode.InviteOnly, manifest.Signup);
        Assert.Equal(["admin", "user"], manifest.DefaultRoles.Select(r => r.Name));
    }

    [Fact]
    public void A_stored_value_this_version_does_not_know_reads_as_invite_only()
    {
        const string stored = """{"permissions":[],"default_roles":[{"name":"admin","permissions":["*"]}],"signup":"open"}""";

        Assert.Equal(SignupMode.InviteOnly, Manifest.FromJson(stored)!.Signup);
    }

    [Fact]
    public void The_built_in_manifest_is_invite_only_and_its_first_admin_role_is_admin()
    {
        Assert.Equal(SignupMode.InviteOnly, Manifest.BuiltIn.Signup);
        Assert.Equal("admin", Manifest.BuiltIn.FirstAdminRole.Name);
    }

    [Fact]
    public void The_first_admin_role_is_the_first_default_role_that_holds_the_star()   // criterion 10
    {
        var manifest = Parsed("""
            permissions: [docs:read]
            default_roles:
              viewer: [docs:read]
              boss: ["*"]
              admin: ["*"]
            """);

        Assert.Equal("boss", manifest.FirstAdminRole.Name);
    }

    [Fact]
    public void Without_a_star_the_first_admin_role_is_the_first_that_holds_members_manage()   // criterion 10
    {
        var manifest = Parsed("""
            permissions: [docs:read]
            default_roles:
              viewer: [docs:read]
              lead: [members:manage, docs:read]
              deputy: [members:manage, roles:manage]
            """);

        Assert.Equal("lead", manifest.FirstAdminRole.Name);
    }

    [Fact]
    public void A_star_role_later_in_the_file_beats_an_earlier_members_manage_role()   // criterion 10
    {
        var manifest = Parsed("""
            permissions: [docs:read]
            default_roles:
              lead: [members:manage]
              admin: ["*"]
            """);

        Assert.Equal("admin", manifest.FirstAdminRole.Name);
    }

    [Fact]
    public void A_manifest_built_by_hand_without_a_managing_role_has_no_first_admin_role()
    {
        var manifest = new Manifest(["docs:read"], [new DefaultRole("viewer", ["docs:read"])]);

        Assert.Throws<InvalidOperationException>(() => manifest.FirstAdminRole);
    }
}
