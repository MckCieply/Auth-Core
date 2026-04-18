using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
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
        Assert.Equal("""{"signup":"self-service","private_signup":"self-service"}""", await BodyAsync(client, "/auth/options"));
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
        Assert.Equal("""{"signup":"invite-only","private_signup":"off"}""", await BodyAsync(client, "/auth/options"));
        Assert.Equal("Degraded", await BodyAsync(client, "/auth/health"));
    }

    [Fact]
    public async Task A_production_host_with_the_v0_1_0_manifest_and_no_address_setting_at_all_stays_invite_only()   // criterion 2: the key is absent, not blank
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment("Production")
            .WithoutSetting(MailSettingsLoader.CompleteSignupUrlKey);
        using var client = factory.CreateClient();

        Assert.False(factory.Services.GetRequiredService<SignupGate>().IsOn);
        Assert.Equal("""{"signup":"invite-only","private_signup":"off"}""", await BodyAsync(client, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(client, "/auth/health"));
        using var request = await SignupApi.Request(client, "anna@firma.test", "Firma");
        await SignupApi.AssertDisabledAsync(request);
    }

    [Fact]
    public async Task A_production_host_with_the_manifest_key_and_an_https_address_has_sign_up_on()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment("Production").WithSignup();
        using var client = factory.CreateClient();

        Assert.Equal("""{"signup":"self-service","private_signup":"off"}""", await BodyAsync(client, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(client, "/auth/health"));
    }
}
