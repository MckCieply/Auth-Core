using Auth.Server.Providers;
using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Manifest: the key <c>providers</c>.</summary>
public sealed class ManifestProvidersTests
{
    private const string Roles = """
        permissions: [docs:read]
        default_roles:
          admin: ["*"]
        """;

    private static Manifest Parsed(string yaml)
    {
        var result = ManifestParser.Parse(yaml);
        Assert.Null(result.Error);
        return Assert.IsType<Manifest>(result.Manifest);
    }

    [Theory]
    [InlineData("[google, microsoft]", "google,microsoft")]
    [InlineData("[microsoft, google]", "microsoft,google")]
    [InlineData("[google]", "google")]
    [InlineData("\n  - microsoft\n  - google", "microsoft,google")]
    public void The_key_lists_the_providers_in_the_order_of_the_file(string list, string expected)   // criterion 1: the order of the buttons
    {
        var manifest = Parsed(Roles + "\nproviders: " + list + "\n");

        Assert.Equal(expected.Split(','), manifest.Providers);
    }

    [Fact]
    public void Duplicates_are_folded_and_the_first_place_is_kept()   // criterion 1
    {
        var manifest = Parsed(Roles + "\nproviders: [microsoft, google, microsoft, google]\n");

        Assert.Equal(["microsoft", "google"], manifest.Providers);
    }

    [Theory]
    [InlineData("[github]")]
    [InlineData("[password]")]
    [InlineData("[google, apple]")]
    [InlineData("[Google]")]
    [InlineData("[\"google \"]")]
    [InlineData("[\"\"]")]
    [InlineData("[~]")]
    [InlineData("google")]
    [InlineData("{google: yes}")]
    public void Any_other_value_makes_the_manifest_invalid(string list)   // criterion 1
    {
        var result = ManifestParser.Parse(Roles + "\nproviders: " + list + "\n");

        Assert.Null(result.Manifest);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void A_refused_name_is_named_in_the_reason()
    {
        var result = ManifestParser.Parse(Roles + "\nproviders: [google, github]\n");

        Assert.Contains("providers", result.Error, StringComparison.Ordinal);
        Assert.Contains("github", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\nproviders:\n")]
    [InlineData("\nproviders: ~\n")]
    [InlineData("\nproviders: []\n")]
    public void No_key_an_empty_list_or_a_key_without_a_value_is_no_provider(string block)   // criterion 1
    {
        Assert.Empty(Parsed(Roles + block).Providers);
    }

    [Fact]
    public void The_stored_form_keeps_the_list_and_its_order()
    {
        var manifest = Parsed(Roles + "\nproviders: [microsoft, google]\n");

        var stored = Manifest.FromJson(manifest.ToJson());

        Assert.NotNull(stored);
        Assert.Equal(["microsoft", "google"], stored.Providers);
    }

    [Fact]
    public void A_manifest_without_providers_is_stored_as_before()   // criterion 30: the stored form of spec 0010 does not change
    {
        Assert.DoesNotContain("providers", Parsed(Roles).ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_stored_by_slice_0010_reads_as_no_provider()   // criterion 1
    {
        const string stored = """{"permissions":["docs:read"],"default_roles":[{"name":"admin","permissions":["*"]}],"signup":"self-service","private":{"signup":"self-service","permissions":["docs:read"]}}""";

        var manifest = Manifest.FromJson(stored);

        Assert.NotNull(manifest);
        Assert.Empty(manifest.Providers);
        Assert.True(manifest.PrivateSignup);
    }

    [Fact]
    public void The_names_are_those_of_the_contract()
    {
        Assert.Equal(["google", "microsoft"], ProviderNames.All);
        Assert.True(ProviderNames.IsName("google"));
        Assert.False(ProviderNames.IsName("Google"));
        Assert.False(ProviderNames.IsName(null));
        Assert.Equal("Microsoft", ProviderNames.DisplayNameOf("microsoft"));
    }
}
