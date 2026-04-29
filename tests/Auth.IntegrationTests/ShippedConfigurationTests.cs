using System.Net;
using System.Text.RegularExpressions;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Providers;
using Auth.Server.Signup;
using Auth.Server.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>The files that ship, read as they are: the settings of Development, the two manifests, the production compose file and its environment.</summary>
public sealed class ShippedConfigurationTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Auth-Core.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, relative);
    }

    private static string DevelopmentAddress()
    {
        var settings = new ConfigurationBuilder().AddJsonFile(RepositoryFile("src/Auth.Server/appsettings.Development.json")).Build();
        return settings[MailSettingsLoader.CompleteSignupUrlKey]
            ?? throw new InvalidOperationException("appsettings.Development.json has no completion address.");
    }

    private static async Task<string> BodyAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// The settings the fake provider's overlay gives Auth-Core: the <c>Auth__...</c> lines of its <c>auth</c> service, as configuration keys.
    /// </summary>
    private static Dictionary<string, string> OverlaySettings()
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(RepositoryFile("tests/Auth.FakeOidc/compose.yml")))
        {
            var trimmed = line.Trim();
            var colon = trimmed.IndexOf(": ", StringComparison.Ordinal);
            if (trimmed.StartsWith("Auth__", StringComparison.Ordinal) && colon > 0)
            {
                settings[trimmed[..colon].Replace("__", ":", StringComparison.Ordinal)] = trimmed[(colon + 2)..];
            }
        }

        return settings;
    }

    /// <summary>
    /// The <c>compose=(...)</c> statement of an end-to-end script, the command that brings its stack up: the files it names are the stack,
    /// the same path in a comment is not.
    /// </summary>
    private static string ComposeCommand(string script)
    {
        var match = Regex.Match(File.ReadAllText(RepositoryFile(script)), @"^compose=\([^)]*\)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"{script} has no compose=(...) statement.");
        return match.Value;
    }

    [Theory]
    [InlineData("deploy/auth.yaml")]
    [InlineData("samples/notes-api/auth.yaml")]
    public void The_shipped_manifests_turn_sign_up_on(string relative)   // spec 0009 → Verification notes
    {
        var result = ManifestParser.Parse(File.ReadAllText(RepositoryFile(relative)));

        Assert.Null(result.Error);
        Assert.Equal(SignupMode.SelfService, result.Manifest!.Signup);
        Assert.Equal("admin", result.Manifest.FirstAdminRole.Name);
        Assert.True(result.Manifest.PrivateSignup);
        Assert.NotEmpty(result.Manifest.PrivatePermissions);
    }

    [Fact]
    public void The_notes_manifest_lists_both_providers_and_the_development_manifest_none()   // spec 0011 → Samples
    {
        var notes = ManifestParser.Parse(File.ReadAllText(RepositoryFile("samples/notes-api/auth.yaml")));
        var development = ManifestParser.Parse(File.ReadAllText(RepositoryFile("deploy/auth.yaml")));

        Assert.Null(notes.Error);
        Assert.Equal(["google", "microsoft"], notes.Manifest!.Providers);
        Assert.Null(development.Error);
        Assert.Empty(development.Manifest!.Providers);
        Assert.Contains("# providers: [google, microsoft]", File.ReadAllText(RepositoryFile("deploy/auth.yaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_fake_overlay_names_the_test_issuer_both_keys_and_the_return_address_and_the_notes_stacks_use_it()   // spec 0011 → Samples
    {
        var settings = OverlaySettings();

        Assert.Equal("http://fake-oidc:8099", settings[ProviderSettings.TestIssuerKey]);
        Assert.Equal("https://localhost:8443/provider-return", settings[ProviderSettings.ReturnUrlKey]);
        Assert.All(
            [ProviderSettings.GoogleClientIdKey, ProviderSettings.GoogleClientSecretKey, ProviderSettings.MicrosoftClientIdKey, ProviderSettings.MicrosoftClientSecretKey],
            key => Assert.False(string.IsNullOrWhiteSpace(settings[key])));
        Assert.All(
            ["scripts/e2e-web.sh", "scripts/e2e-notes.sh", "scripts/e2e-providers.sh"],
            script => Assert.Contains("tests/Auth.FakeOidc/compose.yml", ComposeCommand(script), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_development_host_with_the_notes_manifest_and_the_overlay_offers_both_providers_and_is_healthy()   // spec 0011 → Samples
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithSetting(ManifestSettings.PathKey, "../../samples/notes-api/auth.yaml")
            .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, DevelopmentAddress());
        foreach (var (key, value) in OverlaySettings())
        {
            factory.WithSetting(key, value);
        }

        using var client = factory.CreateClient();

        Assert.Equal(["google", "microsoft"], factory.Services.GetRequiredService<ProviderCatalog>().Offered);
        Assert.Equal(
            """{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft"]}""",
            await BodyAsync(client, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(client, "/auth/health"));
    }

    [Fact]
    public void The_development_settings_name_the_completion_address_of_the_sample()
    {
        Assert.Equal("http://localhost:4200/complete-signup", DevelopmentAddress());
    }

    [Fact]
    public void The_production_compose_file_passes_the_optional_address_and_the_example_lists_it()
    {
        var compose = File.ReadAllText(RepositoryFile("deploy/docker-compose.prod.yml"));
        var example = File.ReadAllLines(RepositoryFile("deploy/.env.prod.example"));

        Assert.Contains("Auth__App__FrontendUrls__CompleteSignup: ${AUTH_FRONTEND_SIGNUP_URL:-}", compose, StringComparison.Ordinal);
        Assert.Contains("AUTH_FRONTEND_SIGNUP_URL=", example);   // a line of its own, blank: the example is a complete list of the variables
    }

    [Fact]
    public void The_sample_overlay_and_the_production_test_set_the_address_so_that_their_stacks_stay_healthy()
    {
        Assert.Contains(
            "Auth__App__FrontendUrls__CompleteSignup: http://localhost:8088/complete-signup",
            File.ReadAllText(RepositoryFile("samples/notes-web/compose.yml")),
            StringComparison.Ordinal);
        Assert.Contains(
            "AUTH_FRONTEND_SIGNUP_URL=$PROXY_URL/complete-signup",
            File.ReadAllText(RepositoryFile("scripts/e2e-prod.sh")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_with_the_shipped_manifest_and_the_development_address_has_sign_up_on_and_is_healthy()
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithSetting(ManifestSettings.PathKey, "../../deploy/auth.yaml")
            .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, DevelopmentAddress());
        using var client = factory.CreateClient();

        Assert.True(factory.Services.GetRequiredService<SignupGate>().IsOn);
        Assert.Equal("""{"signup":"self-service","private_signup":"self-service","providers":[]}""", await BodyAsync(client, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(client, "/auth/health"));
    }

    [Fact]
    public async Task A_production_host_with_the_manifest_key_and_an_empty_address_starts_with_sign_up_off()   // Review Focus 1: compose passes an empty variable
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment("Production")
            .WithManifest(AuthAppFactory.SelfServiceManifest)
            .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, "");
        using var client = factory.CreateClient();

        Assert.False(factory.Services.GetRequiredService<SignupGate>().IsOn);
        Assert.Equal("""{"signup":"invite-only","private_signup":"off","providers":[]}""", await BodyAsync(client, "/auth/options"));
        Assert.Equal("Degraded", await BodyAsync(client, "/auth/health"));
    }

    [Fact]
    public async Task A_production_host_with_the_v0_1_0_manifest_and_no_address_setting_at_all_stays_invite_only()   // criterion 2: the key is absent, not blank
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment("Production")
            .WithoutSetting(MailSettingsLoader.CompleteSignupUrlKey);
        using var client = factory.CreateClient();

        Assert.False(factory.Services.GetRequiredService<SignupGate>().IsOn);
        Assert.Equal("""{"signup":"invite-only","private_signup":"off","providers":[]}""", await BodyAsync(client, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(client, "/auth/health"));
        using var request = await SignupApi.Request(client, "anna@firma.test", "Firma");
        await SignupApi.AssertDisabledAsync(request);
    }

    [Fact]
    public async Task A_production_host_with_the_manifest_key_and_an_https_address_has_sign_up_on()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment("Production").WithSignup();
        using var client = factory.CreateClient();

        Assert.Equal("""{"signup":"self-service","private_signup":"off","providers":[]}""", await BodyAsync(client, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(client, "/auth/health"));
    }
}
