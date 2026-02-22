using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

public sealed class ManifestParserTests
{
    private const string Valid = """
        permissions: [reports:read, reports:approve, templates:manage]
        default_roles:
          admin: ["*"]
          user:  [reports:read, reports:approve]
        """;

    // Spelled out, so that the escapes the tests expect in a reason are not read as escapes of the test's own strings.
    private const string Backslash = "\\";

    private static string Error(string yaml)
    {
        var result = ManifestParser.Parse(yaml);

        Assert.Null(result.Manifest);
        return Assert.IsType<string>(result.Error);
    }

    [Fact]
    public void Manifest_of_the_spec_is_read()   // criterion 20
    {
        var result = ManifestParser.Parse(Valid);

        Assert.Null(result.Error);
        var manifest = Assert.IsType<Manifest>(result.Manifest);
        Assert.Equal(["reports:read", "reports:approve", "templates:manage"], manifest.Permissions);
        Assert.Equal(["admin", "user"], manifest.DefaultRoles.Select(r => r.Name));
        Assert.Equal(["*"], manifest.DefaultRoles[0].Permissions);
        Assert.Equal(["reports:read", "reports:approve"], manifest.DefaultRoles[1].Permissions);
    }

    [Fact]
    public void Catalog_is_the_declared_permissions_plus_the_built_in_ones()
    {
        var manifest = ManifestParser.Parse(Valid).Manifest!;

        Assert.Equal(
            ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            manifest.Catalog.Permissions);
        Assert.Equal("*", manifest.Catalog.Listed[0]);
        Assert.Equal(8, manifest.Catalog.Listed.Count);
    }

    [Fact]
    public void Built_in_permissions_may_be_listed_and_are_not_duplicated_in_the_catalog()
    {
        var manifest = ManifestParser.Parse("""
            permissions: [members:manage, docs:read]
            default_roles:
              owner: [members:manage]
            """).Manifest!;

        Assert.Equal(["docs:read", "members:manage", "org:delete", "org:manage", "roles:manage"], manifest.Catalog.Permissions);
    }

    [Fact]
    public void A_manifest_without_permissions_has_the_built_in_catalog()
    {
        var manifest = ManifestParser.Parse("""
            default_roles:
              owner: ["*"]
            """).Manifest!;

        Assert.Equal(PermissionCatalog.BuiltIn, manifest.Catalog.Permissions);
    }

    [Fact]
    public void Built_in_manifest_has_one_default_role_holding_everything()
    {
        Assert.Empty(Manifest.BuiltIn.Permissions);
        var role = Assert.Single(Manifest.BuiltIn.DefaultRoles);
        Assert.Equal("admin", role.Name);
        Assert.Equal(["*"], role.Permissions);
    }

    [Fact]
    public void Manifest_survives_the_form_kept_in_the_database()
    {
        var manifest = ManifestParser.Parse(Valid).Manifest!;

        var back = Manifest.FromJson(manifest.ToJson());

        Assert.NotNull(back);
        Assert.Equal(manifest.Permissions, back.Permissions);
        Assert.Equal(
            manifest.DefaultRoles.Select(r => (r.Name, string.Join(',', r.Permissions))),
            back.DefaultRoles.Select(r => (r.Name, string.Join(',', r.Permissions))));
        Assert.Null(Manifest.FromJson("not json"));
    }

    [Theory]
    [InlineData("Reports:read")]            // uppercase
    [InlineData("1reports")]                // starts with a digit
    [InlineData(":reports")]                // starts with a colon
    [InlineData("reports read")]            // a space
    [InlineData("reports.read")]            // a dot
    [InlineData("zażółć")]                  // not ASCII
    [InlineData("")]                        // empty
    [InlineData("*")]                       // the wildcard is not a permission to declare
    public void Malformed_permission_makes_the_manifest_invalid(string permission)   // criterion 20
    {
        var yaml = $"""
            permissions: ["{permission}"]
            default_roles:
              admin: ["*"]
            """;

        Assert.Contains("permission", Error(yaml));
    }

    [Fact]
    public void Permission_of_64_characters_is_accepted_and_of_65_is_not()
    {
        var sixtyFour = "a" + new string('b', 63);
        Assert.Null(ManifestParser.Parse($"permissions: [{sixtyFour}]\ndefault_roles:\n  admin: [\"*\"]\n").Error);
        Assert.NotNull(ManifestParser.Parse($"permissions: [{sixtyFour}b]\ndefault_roles:\n  admin: [\"*\"]\n").Error);
    }

    [Fact]
    public void Permission_with_a_trailing_newline_is_malformed()
    {
        Assert.False(PermissionCatalog.IsWellFormed("reports:read\n"));
        Assert.True(PermissionCatalog.IsWellFormed("reports:read"));
    }

    [Fact]
    public void Default_role_with_a_permission_outside_the_catalog_makes_the_manifest_invalid()
    {
        var error = Error("""
            permissions: [reports:read]
            default_roles:
              admin: ["*"]
              user: [reports:write]
            """);

        Assert.Contains("reports:write", error);
        Assert.Contains("user", error);
    }

    [Fact]
    public void Default_role_may_hold_a_built_in_permission_that_is_not_listed()
    {
        var result = ManifestParser.Parse("""
            default_roles:
              owner: [members:manage, org:manage]
            """);

        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("default_roles:\n  user: []\n")]                                    // nobody can manage members
    [InlineData("permissions: [a:b]\ndefault_roles:\n  user: [a:b]\n")]             // same
    [InlineData("default_roles:\n  user: [roles:manage, org:manage]\n")]            // manages roles, not members
    public void At_least_one_default_role_must_manage_members(string yaml)   // criterion 20
    {
        Assert.Contains("members:manage", Error(yaml));
    }

    [Theory]
    [InlineData("")]
    [InlineData("permissions: [a:b]\n")]                                            // no default_roles
    [InlineData("permissions: [a:b]\ndefault_roles: {}\n")]
    [InlineData("- just\n- a list\n")]
    [InlineData("just a string\n")]
    [InlineData("permissions: [a:b\ndefault_roles: [\n")]                           // not YAML
    [InlineData("permissions: {a: b}\ndefault_roles:\n  admin: [\"*\"]\n")]         // wrong shape
    [InlineData("permissions: [[a:b]]\ndefault_roles:\n  admin: [\"*\"]\n")]        // a list inside the list
    [InlineData("permisions: [a:b]\ndefault_roles:\n  admin: [\"*\"]\n")]           // a typo is an unknown key, not an empty list
    [InlineData("default_roles:\n  admin: [\"*\"]\napp:\n  name: x\n")]             // the old keys of design.md are not accepted
    public void Text_that_is_not_a_manifest_is_refused_with_a_reason(string yaml)   // criterion 20
    {
        Assert.False(string.IsNullOrWhiteSpace(Error(yaml)));
    }

    [Fact]
    public void Duplicate_keys_are_refused()
    {
        Assert.NotNull(ManifestParser.Parse("default_roles:\n  admin: [\"*\"]\ndefault_roles:\n  user: [\"*\"]\n").Error);
        Assert.NotNull(ManifestParser.Parse("default_roles:\n  admin: [\"*\"]\n  admin: [\"*\"]\n").Error);
    }

    [Fact]
    public void Anchors_aliases_and_a_second_document_are_refused()
    {
        Assert.Contains("anchors", Error("permissions: &p [a:b]\ndefault_roles:\n  admin: [\"*\"]\n"));
        Assert.Contains("aliases", Error("permissions: *p\ndefault_roles:\n  admin: [\"*\"]\n"));
        Assert.Contains("more than one document", Error("default_roles:\n  admin: [\"*\"]\n---\ndefault_roles:\n  admin: [\"*\"]\n"));
    }

    [Fact]
    public void Role_names_follow_the_rules_for_names()
    {
        Assert.Contains("name", Error("default_roles:\n  \" admin\": [\"*\"]\n"));
        Assert.Contains("name", Error($"default_roles:\n  {new string('a', 101)}: [\"*\"]\n"));
        // A raw string: the YAML itself holds the escape, which the parser turns into a control character.
        Assert.Contains("name", Error("""
            default_roles:
              "ad\u0007min": ["*"]
            """));
        Assert.Null(ManifestParser.Parse($"default_roles:\n  {new string('a', 100)}: [\"*\"]\n").Error);
        Assert.Null(ManifestParser.Parse("default_roles:\n  Kierownik żółw: [\"*\"]\n").Error);
    }

    [Fact]
    public void A_role_name_with_a_format_character_is_refused()
    {
        // A raw string: the YAML holds the escape of a zero-width space, which would make the role look like admin.
        Assert.Contains("name", Error("""
            default_roles:
              admin: ["*"]
              "admin\u200B": []
            """));
    }

    [Fact]
    public void A_refused_name_reaches_the_reason_with_its_line_breaks_and_control_characters_escaped()   // the reason is logged
    {
        // Raw strings: the YAML holds the escapes, which the parser turns into a line feed, an escape character and a
        // line separator.
        var permission = Error("""
            permissions: ["bad\nline"]
            default_roles:
              admin: ["*"]
            """);
        var role = Error("""
            default_roles:
              "ad\emin": ["*"]
            """);
        var separated = Error("""
            default_roles:
              admin: ["*"]
              "ad\Lmin": [nothing:here]
            """);

        Assert.All(new[] { permission, role, separated }, reason => Assert.DoesNotContain(reason, c => char.IsControl(c) || c == (char)0x2028));
        Assert.Contains("'bad" + Backslash + "u000Aline'", permission);
        Assert.Contains("'ad" + Backslash + "u001Bmin'", role);
        Assert.Contains("'ad" + Backslash + "u2028min'", separated);
    }

    [Fact]
    public void Role_names_that_differ_only_in_case_are_a_duplicate()
    {
        Assert.Contains("twice", Error("default_roles:\n  admin: [\"*\"]\n  Admin: [\"*\"]\n"));
    }

    [Fact]
    public void Role_without_a_value_holds_nothing()
    {
        var manifest = ManifestParser.Parse("default_roles:\n  admin: [\"*\"]\n  guest:\n").Manifest!;

        Assert.Empty(manifest.DefaultRoles.Single(r => r.Name == "guest").Permissions);
    }

    [Fact]
    public void Repeated_permissions_are_kept_once()
    {
        var manifest = ManifestParser.Parse("permissions: [a:b, a:b]\ndefault_roles:\n  admin: [\"*\", \"*\", a:b]\n").Manifest!;

        Assert.Equal(["a:b"], manifest.Permissions);
        Assert.Equal(["*"], manifest.DefaultRoles[0].Permissions);   // star holds the rest
    }

    [Fact]
    public void A_role_that_lists_star_is_star_alone()
    {
        var manifest = ManifestParser.Parse("permissions: [a:b]\ndefault_roles:\n  admin: [a:b, \"*\"]\n  user: [a:b]\n").Manifest!;

        Assert.Equal(["*"], manifest.DefaultRoles[0].Permissions);
        Assert.Equal(["a:b"], manifest.DefaultRoles[1].Permissions);
    }

    [Fact]
    public void Default_roles_keep_the_order_of_the_file()
    {
        var manifest = ManifestParser.Parse("permissions: [a:b]\ndefault_roles:\n  zeta: [a:b]\n  admin: [\"*\"]\n  alpha: [a:b]\n").Manifest!;

        Assert.Equal(["zeta", "admin", "alpha"], manifest.DefaultRoles.Select(r => r.Name));
    }

    [Fact]
    public void Manifest_with_101_default_roles_is_refused_and_with_100_is_accepted()
    {
        static string Roles(int count) =>
            "default_roles:\n  admin: [\"*\"]\n" + string.Concat(Enumerable.Range(1, count - 1).Select(i => $"  r{i}: []\n"));

        Assert.Null(ManifestParser.Parse(Roles(100)).Error);
        Assert.Contains("100", Error(Roles(101)));
    }

    [Fact]
    public void Manifest_with_too_many_permissions_is_refused()
    {
        var many = string.Join(", ", Enumerable.Range(0, 501).Select(i => "p" + i));

        Assert.Contains("500", Error($"permissions: [{many}]\ndefault_roles:\n  admin: [\"*\"]\n"));
    }
}
