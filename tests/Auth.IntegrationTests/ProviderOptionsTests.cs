using System.Net;
using System.Text.Json;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;
using Auth.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>GET /auth/options</c>: <c>providers</c> lists exactly the offered providers, in the manifest's order.</summary>
public sealed class ProviderOptionsTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static async Task<string> BodyAsync(AuthAppFactory factory, string path)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string[]> ProvidersAsync(AuthAppFactory factory)
    {
        using var body = JsonDocument.Parse(await BodyAsync(factory, "/auth/options"));
        return [.. body.RootElement.GetProperty("providers").EnumerateArray().Select(e => e.GetString()!)];
    }

    [Fact]
    public async Task With_no_key_providers_is_an_empty_list()   // criterion 2
    {
        await using var factory = new AuthAppFactory(postgres, keys);

        Assert.Equal("""{"signup":"invite-only","private_signup":"off","providers":[]}""", await BodyAsync(factory, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(factory, "/auth/health"));
    }

    [Theory]
    [InlineData("[google, microsoft]", "google,microsoft")]
    [InlineData("[microsoft, google]", "microsoft,google")]
    [InlineData("[microsoft]", "microsoft")]
    public async Task The_offered_providers_are_listed_in_the_order_of_the_manifest(string list, string expected)   // criterion 2
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + "\nproviders: " + list + "\n")
            .WithProviderKeys();

        Assert.Equal(expected.Split(','), await ProvidersAsync(factory));
        Assert.Equal("Healthy", await BodyAsync(factory, "/auth/health"));
    }

    [Theory]
    [InlineData(ProviderSettings.GoogleClientIdKey, "microsoft")]
    [InlineData(ProviderSettings.GoogleClientSecretKey, "microsoft")]
    [InlineData(ProviderSettings.MicrosoftClientSecretKey, "google")]
    [InlineData(ProviderSettings.ReturnUrlKey, "")]
    public async Task A_listed_provider_without_its_id_its_secret_or_the_return_address_is_left_out(string blank, string expected)   // criterion 2
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest)
            .WithProviders()
            .WithSetting(blank, "");

        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), await ProvidersAsync(factory));
        Assert.Equal("Degraded", await BodyAsync(factory, "/auth/health"));
    }

    [Fact]
    public async Task IsOffered_is_true_only_for_a_listed_provider_with_its_keys()   // criterion 2
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + "\nproviders: [google]\n")
            .WithProviderKeys();
        using var client = factory.CreateClient();   // starts the host
        var catalog = factory.Services.GetRequiredService<ProviderCatalog>();

        Assert.True(catalog.IsOffered(ProviderNames.Google));
        Assert.False(catalog.IsOffered(ProviderNames.Microsoft));   // keyed, not listed
        Assert.False(catalog.IsOffered("github"));

        await using var unkeyed = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.ProvidersLine);
        using var other = unkeyed.CreateClient();

        Assert.False(unkeyed.Services.GetRequiredService<ProviderCatalog>().IsOffered(ProviderNames.Google));   // listed, not keyed
    }

    [Fact]
    public async Task A_configured_provider_that_is_not_listed_is_not_offered_and_health_is_healthy()   // criterion 2
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithProviderKeys();

        Assert.Empty(await ProvidersAsync(factory));
        Assert.Equal("Healthy", await BodyAsync(factory, "/auth/health"));
    }

    [Fact]
    public async Task A_manifest_with_an_unknown_provider_keeps_the_last_valid_one_and_health_says_degraded()   // criterion 1
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        await using (var first = new AuthAppFactory(postgres, keys, database)
            .WithManifest(AuthAppFactory.DefaultManifest + "\nproviders: [google]\n")
            .WithProviderKeys())
        {
            Assert.Equal(["google"], await ProvidersAsync(first));
        }

        await using var second = new AuthAppFactory(postgres, keys, database)
            .WithManifest(AuthAppFactory.DefaultManifest + "\nproviders: [google, github]\n")
            .WithProviderKeys();

        Assert.Equal(["google"], await ProvidersAsync(second));   // the stored manifest of the first start
        Assert.Equal("Degraded", await BodyAsync(second, "/auth/health"));
        Assert.True(second.Services.GetRequiredService<ManifestHolder>().State.IsDegraded);
    }
}
