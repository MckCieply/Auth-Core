using System.Globalization;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Configuration: the provider settings, the warning, health and the secret's date.</summary>
public sealed class ProviderSettingsTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static ProviderSettings Load(Dictionary<string, string?> values, bool isDevelopment = false) =>
        ProviderSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), isDevelopment);

    private static Dictionary<string, string?> Configured() => new()
    {
        [ProviderSettings.ReturnUrlKey] = "https://app.example.com/provider-return",
        [ProviderSettings.GoogleClientIdKey] = AuthAppFactory.GoogleClientId,
        [ProviderSettings.GoogleClientSecretKey] = AuthAppFactory.GoogleClientSecret,
        [ProviderSettings.MicrosoftClientIdKey] = AuthAppFactory.MicrosoftClientId,
        [ProviderSettings.MicrosoftClientSecretKey] = AuthAppFactory.MicrosoftClientSecret,
    };

    private static async Task<string> BodyAsync(AuthAppFactory factory, string path)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Every warning the catalog wrote: the start's "not offered" lines and the secret's line.</summary>
    private static List<(LogLevel Level, string Category, string Message)> ProviderWarnings(CapturingLoggerProvider logs) =>
        [.. logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Category == typeof(ProviderCatalog).FullName)];

    [Fact]
    public void The_keys_are_the_ones_of_the_contract()
    {
        Assert.Equal("Auth:App:FrontendUrls:ProviderReturn", ProviderSettings.ReturnUrlKey);
        Assert.Equal("Auth:Providers:Google:ClientId", ProviderSettings.GoogleClientIdKey);
        Assert.Equal("Auth:Providers:Google:ClientSecret", ProviderSettings.GoogleClientSecretKey);
        Assert.Equal("Auth:Providers:Microsoft:ClientId", ProviderSettings.MicrosoftClientIdKey);
        Assert.Equal("Auth:Providers:Microsoft:ClientSecret", ProviderSettings.MicrosoftClientSecretKey);
        Assert.Equal("Auth:Providers:Microsoft:ClientSecretExpiresOn", ProviderSettings.MicrosoftSecretExpiresOnKey);
        Assert.Equal("Auth:Providers:TestIssuer", ProviderSettings.TestIssuerKey);
    }

    [Fact]
    public void Nothing_set_is_valid_and_names_every_missing_key()   // criterion 3: missing keys never stop the start
    {
        var settings = Load([]);

        Assert.Null(settings.ReturnUrl);
        Assert.Null(settings.Google);
        Assert.Null(settings.Microsoft);
        Assert.Null(settings.MicrosoftSecretExpiresOn);
        Assert.Null(settings.TestIssuer);
        Assert.Equal(
            [ProviderSettings.GoogleClientIdKey, ProviderSettings.GoogleClientSecretKey, ProviderSettings.ReturnUrlKey],
            settings.MissingKeysOf(ProviderNames.Google));
    }

    [Fact]
    public void Both_keys_and_the_return_address_leave_nothing_missing()
    {
        var settings = Load(Configured());

        Assert.Empty(settings.MissingKeysOf(ProviderNames.Google));
        Assert.Empty(settings.MissingKeysOf(ProviderNames.Microsoft));
        Assert.Equal(AuthAppFactory.GoogleClientId, settings.KeysOf(ProviderNames.Google)?.ClientId);
        Assert.Equal(AuthAppFactory.MicrosoftClientSecret, settings.KeysOf(ProviderNames.Microsoft)?.ClientSecret);
        Assert.Equal(new Uri("https://app.example.com"), settings.ReturnOrigin);
    }

    [Theory]
    [InlineData("", "secret", ProviderSettings.GoogleClientIdKey)]
    [InlineData("   ", "secret", ProviderSettings.GoogleClientIdKey)]
    [InlineData("id", "", ProviderSettings.GoogleClientSecretKey)]
    [InlineData("id", null, ProviderSettings.GoogleClientSecretKey)]
    public void A_blank_id_or_secret_is_missing_and_named(string? id, string? secret, string named)   // criterion 3
    {
        var values = Configured();
        values[ProviderSettings.GoogleClientIdKey] = id;
        values[ProviderSettings.GoogleClientSecretKey] = secret;

        var settings = Load(values);

        Assert.Null(settings.Google);
        Assert.Equal([named], settings.MissingKeysOf(ProviderNames.Google));
        Assert.Empty(settings.MissingKeysOf(ProviderNames.Microsoft));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/provider-return")]
    [InlineData("ftp://app.example.com/provider-return")]
    [InlineData("http://app.example.com/provider-return")]   // http outside Development
    [InlineData("https://app.example.com/provider-return#top")]
    [InlineData("https://user:secret@app.example.com/provider-return")]
    [InlineData("https://app.example.com/provider-return?token=abc")]
    [InlineData("https://app.example.com/provider-return?a=1&ticket=abc")]
    public void A_bad_return_address_is_refused_naming_the_key_and_never_echoing_the_value(string value)   // criterion 3
    {
        var values = Configured();
        values[ProviderSettings.ReturnUrlKey] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains(ProviderSettings.ReturnUrlKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_http_return_address_is_accepted_in_development()
    {
        var values = Configured();
        values[ProviderSettings.ReturnUrlKey] = "http://localhost:8088/provider-return";

        Assert.Equal(new Uri("http://localhost:8088"), Load(values, isDevelopment: true).ReturnOrigin);
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("01.05.2026")]
    [InlineData("tomorrow")]
    [InlineData("2026-5-1")]
    [InlineData("2026-05-01T00:00")]
    public void A_secret_date_that_is_not_yyyy_mm_dd_is_refused_naming_the_key(string value)   // criterion 3
    {
        var values = Configured();
        values[ProviderSettings.MicrosoftSecretExpiresOnKey] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains(ProviderSettings.MicrosoftSecretExpiresOnKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_date_loads()
    {
        var values = Configured();
        values[ProviderSettings.MicrosoftSecretExpiresOnKey] = "2027-04-30";

        Assert.Equal(new DateOnly(2027, 4, 30), Load(values).MicrosoftSecretExpiresOn);
    }

    [Fact]
    public void The_test_issuer_outside_development_is_refused_naming_the_key()   // criterion 3, Settled 23
    {
        var values = Configured();
        values[ProviderSettings.TestIssuerKey] = "http://fake-oidc:8099";

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains(ProviderSettings.TestIssuerKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(new Uri("http://fake-oidc:8099"), Load(values, isDevelopment: true).TestIssuer);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://fake-oidc:8099")]
    [InlineData("http://fake-oidc:8099/?a=1")]
    [InlineData("http://user:pw@fake-oidc:8099")]
    public void A_test_issuer_that_is_not_an_http_address_is_refused_naming_the_key(string value)
    {
        var values = Configured();
        values[ProviderSettings.TestIssuerKey] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values, isDevelopment: true));

        Assert.Contains(ProviderSettings.TestIssuerKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_listed_provider_without_its_keys_warns_once_naming_them_and_health_says_degraded()   // criterion 3, Review Focus 3
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.ProvidersLine)
            .WithProviderKeys()
            .WithSetting(ProviderSettings.GoogleClientIdKey, "")
            .WithSetting(ProviderSettings.GoogleClientSecretKey, "")
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Equal("""{"signup":"invite-only","private_signup":"off","providers":["microsoft"]}""", await BodyAsync(factory, "/auth/options"));
        Assert.Equal("Degraded", await BodyAsync(factory, "/auth/health"));
        var warning = Assert.Single(ProviderWarnings(logs));   // one warning in all: none for Microsoft, which is configured
        Assert.Contains("google", warning.Message, StringComparison.Ordinal);
        Assert.Contains(ProviderSettings.GoogleClientIdKey, warning.Message, StringComparison.Ordinal);
        Assert.Contains(ProviderSettings.GoogleClientSecretKey, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ProviderSettings.ReturnUrlKey, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(AuthAppFactory.MicrosoftClientSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(AuthAppFactory.MicrosoftClientId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Listed_providers_without_the_return_address_warn_once_each_naming_the_key()   // criterion 3
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.ProvidersLine)
            .WithProviderKeys()
            .WithSetting(ProviderSettings.ReturnUrlKey, "")
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Equal("Degraded", await BodyAsync(factory, "/auth/health"));
        var warnings = ProviderWarnings(logs);
        Assert.Equal(2, warnings.Count);
        Assert.Single(warnings, w => w.Message.Contains("google", StringComparison.Ordinal));
        Assert.Single(warnings, w => w.Message.Contains("microsoft", StringComparison.Ordinal));
        Assert.All(warnings, w => Assert.Contains(ProviderSettings.ReturnUrlKey, w.Message, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(AuthAppFactory.GoogleClientSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bad_return_address_stops_the_start_naming_the_key()   // criterion 3
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting(ProviderSettings.ReturnUrlKey, "https://app.example.com/back#x");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(ProviderSettings.ReturnUrlKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_secret_date_stops_the_start_naming_the_key()   // criterion 3
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting(ProviderSettings.MicrosoftSecretExpiresOnKey, "30.04.2027");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(ProviderSettings.MicrosoftSecretExpiresOnKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_test_issuer_outside_development_stops_the_start_naming_the_key()   // criterion 3
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithEnvironment("Production")
            .WithSetting(ProviderSettings.TestIssuerKey, "http://fake-oidc:8099");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(ProviderSettings.TestIssuerKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(31, "Healthy")]
    [InlineData(30, "Degraded")]
    [InlineData(1, "Degraded")]
    [InlineData(0, "Degraded")]
    [InlineData(-1, "Degraded")]
    public async Task The_secret_date_makes_health_degraded_from_thirty_days_before(int daysAhead, string health)   // criterion 4, Review Focus 3
    {
        var now = DateTimeOffset.UtcNow;
        var date = DateOnly.FromDateTime(now.UtcDateTime).AddDays(daysAhead);
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithClock(new FakeTimeProvider(now))
            .WithSetting(ProviderSettings.MicrosoftSecretExpiresOnKey, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        Assert.Equal(health, await BodyAsync(factory, "/auth/health"));
    }

    [Fact]
    public async Task The_secret_date_warns_at_the_start_naming_the_key_and_the_date()   // spec 0011 → Configuration
    {
        var logs = new CapturingLoggerProvider();
        var now = DateTimeOffset.UtcNow;
        var date = DateOnly.FromDateTime(now.UtcDateTime).AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithClock(new FakeTimeProvider(now))
            .WithSetting(ProviderSettings.MicrosoftSecretExpiresOnKey, date)
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Equal("Degraded", await BodyAsync(factory, "/auth/health"));
        Assert.Single(
            logs.Entries,
            e => e.Level == LogLevel.Warning
                && e.Message.Contains(ProviderSettings.MicrosoftSecretExpiresOnKey, StringComparison.Ordinal)
                && e.Message.Contains(date, StringComparison.Ordinal));
    }
}
