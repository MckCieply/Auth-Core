using Auth.Infrastructure.Identity;
using Auth.Server.Email;
using Auth.Server.Network;
using Auth.Server.RateLimiting;
using Auth.Server.Seeding;
using Auth.Server.Tenancy;
using TokenOptions = Auth.Server.Tokens.TokenOptions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Auth.IntegrationTests.Infrastructure;

public class AuthAppFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings = new();
    private string? _environment;
    private TimeProvider? _clock;
    private readonly List<Action<IServiceCollection>> _serviceOverrides = [];
    private readonly string _manifestDirectory;

    /// <param name="postgres">The shared Postgres server.</param>
    /// <param name="keys">Throwaway signing/encryption key material the host loads from disk.</param>
    /// <param name="database">
    /// Database name on that server. <see langword="null"/> (default) gives this factory a fresh, unique
    /// database so tests never see each other's data. Pass an explicit name to start several factories
    /// on the SAME database (restart scenarios).
    /// </param>
    public AuthAppFactory(PostgresFixture postgres, KeyMaterialFixture keys, string? database = null)
    {
        DatabaseName = database ?? "auth_" + Guid.NewGuid().ToString("N");
        _settings["ConnectionStrings:Auth"] = postgres.ConnectionStringFor(DatabaseName);
        _settings["Auth:Database:MigrateOnStartup"] = "true";
        _settings["Auth:Keys:SigningCertificatePath"] = keys.SigningCertPath;
        _settings["Auth:Keys:SigningKeyPath"] = keys.SigningKeyPath;
        _settings["Auth:Keys:EncryptionCertificatePath"] = keys.EncryptionCertPath;
        _settings["Auth:Keys:EncryptionKeyPath"] = keys.EncryptionKeyPath;
        // Pinned explicitly (UseSetting beats environment variables), so a stray Auth__Tokens__* variable on the
        // machine running the tests cannot change what the tests expect, and Production-mode hosts pass the
        // "identifiers must be explicit" check.
        _settings[TokenOptions.IssuerKey] = "http://localhost:8080/auth";
        _settings[TokenOptions.AudienceKey] = "auth-core-dev";
        _settings[DevUserSeeder.EmailKey] = DefaultSeedEmail;
        _settings[DevUserSeeder.PasswordKey] = DefaultSeedPassword;
        // Mail: pinned like the token identifiers. The server name does not resolve: a host that is not given a
        // transport of its own (MailTestBase) or a real catcher (MailpitFixture) can never send anything.
        _settings[MailSettingsLoader.AppNameKey] = DefaultAppName;
        _settings[MailSettingsLoader.LocaleKey] = "en";
        _settings[MailSettingsLoader.ResetPasswordUrlKey] = DefaultResetUrl;
        _settings[MailSettingsLoader.VerifyEmailUrlKey] = DefaultVerifyUrl;
        _settings[MailSettingsLoader.AcceptInviteUrlKey] = DefaultInviteUrl;
        _settings[MailSettingsLoader.FromKey] = DefaultFrom;
        _settings[MailSettingsLoader.SmtpHostKey] = "smtp.invalid";
        _settings[MailSettingsLoader.SmtpPortKey] = "587";
        _settings[MailSettingsLoader.SmtpSecurityKey] = "starttls";
        // Empty counts as unset for the loader and the seeder: an Auth__Email__Smtp__* or Auth__DevSeed__Unverified*
        // variable on the machine cannot add a login or a second user to a test host. A test that wants them sets
        // them with WithSetting.
        _settings[MailSettingsLoader.SmtpUsernameKey] = "";
        _settings[MailSettingsLoader.SmtpPasswordKey] = "";
        _settings[DevUserSeeder.UnverifiedEmailKey] = "";
        _settings[DevUserSeeder.UnverifiedPasswordKey] = "";
        // The per-IP limiter is off in a test host, so that no test meets it by accident (the test server sends everything from one
        // address, "unknown"); a test about it turns it on with WithSetting. Blank proxy lists count as unset, so that a variable of
        // the machine cannot make a test host trust a proxy.
        _settings[RateLimitSettings.EnabledKey] = "false";
        _settings[ProxySettings.KnownNetworksKey] = "";
        _settings[ProxySettings.KnownProxiesKey] = "";
        // The manifest: a file of this host's own, so that a test changes it without touching another's.
        _manifestDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "auth-core-manifest-" + Guid.NewGuid().ToString("N"))).FullName;
        ManifestPath = Path.Combine(_manifestDirectory, "auth.yaml");
        File.WriteAllText(ManifestPath, DefaultManifest);
        _settings[ManifestSettings.PathKey] = ManifestPath;
    }

    /// <summary>Default development seed credentials; the password satisfies Identity's default policy.</summary>
    public const string DefaultSeedEmail = "user@example.com";

    public const string DefaultSeedPassword = "Correct-Horse-Battery-1";

    /// <summary>The manifest every test host starts with: the example of spec 0005.</summary>
    public const string DefaultManifest = """
        permissions: [reports:read, reports:approve, templates:manage]
        default_roles:
          admin: ["*"]
          user: [reports:read, reports:approve]
        """;

    public const string DefaultAppName = "Auth-Core Test";
    public const string DefaultResetUrl = "https://app.example.com/reset";
    public const string DefaultVerifyUrl = "https://app.example.com/verify";
    public const string DefaultInviteUrl = "https://app.example.com/invite";
    public const string DefaultFrom = "no-reply@example.com";

    /// <summary>The effective seed email: the default, or the value set via <see cref="WithSetting"/>.</summary>
    public string SeedEmail => _settings[DevUserSeeder.EmailKey] ?? "";

    /// <summary>The effective seed password: the default, or the value set via <see cref="WithSetting"/>.</summary>
    public string SeedPassword => _settings[DevUserSeeder.PasswordKey] ?? "";

    /// <summary>Id of the user created by the development seed (looked up by <see cref="SeedEmail"/>).</summary>
    public async Task<Guid> SeedUserIdAsync()
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(SeedEmail)
            ?? throw new InvalidOperationException($"Seed user '{SeedEmail}' does not exist.");
        return user.Id;
    }

    public string DatabaseName { get; }

    /// <summary>The connection string of this host's database, for a test that runs the operator CLI against it.</summary>
    public string ConnectionString => _settings["ConnectionStrings:Auth"] ?? "";

    /// <summary>The manifest file of this host.</summary>
    public string ManifestPath { get; }

    /// <summary>Replaces the content of the manifest file; it is read when the host starts.</summary>
    public AuthAppFactory WithManifest(string yaml)
    {
        File.WriteAllText(ManifestPath, yaml);
        return this;
    }

    /// <summary>Removes the manifest file, as if the product had not shipped one.</summary>
    public AuthAppFactory WithoutManifestFile()
    {
        File.Delete(ManifestPath);
        return this;
    }

    public AuthAppFactory WithSetting(string key, string? value)
    {
        _settings[key] = value;
        return this;
    }

    /// <summary>Leaves <paramref name="key"/> out of the configuration altogether (also undoing a default pin).</summary>
    public AuthAppFactory WithoutSetting(string key)
    {
        _settings.Remove(key);
        return this;
    }

    /// <summary>The header that names the client address of a request in a host made with <see cref="WithRemoteAddressHeader"/>.</summary>
    public const string RemoteAddressHeader = "X-Test-Remote-Address";

    /// <summary>
    /// Lets a request say, in <see cref="RemoteAddressHeader"/>, which address it comes from: the test server has none. Call it in
    /// the constructor of the test, before the host is built.
    /// </summary>
    public AuthAppFactory WithRemoteAddressHeader() =>
        WithServices(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, RemoteAddressFilter>());

    /// <summary>Replaces the host's clock, so a test can move time forward.</summary>
    public AuthAppFactory WithClock(TimeProvider clock)
    {
        _clock = clock;
        return this;
    }

    /// <summary>Changes the host's service registrations after the app has made its own.</summary>
    public AuthAppFactory WithServices(Action<IServiceCollection> configure)
    {
        _serviceOverrides.Add(configure);
        return this;
    }

    /// <summary>
    /// Leaves a background service out of the host, for tests that drive its work by hand. Stopping the service on a
    /// running host instead is a race: stopped before its loop has started, it ends as a cancelled task, which the host
    /// takes for a failed background service and stops itself.
    /// </summary>
    public AuthAppFactory WithoutHostedService<TService>()
        where TService : class, IHostedService =>
        WithServices(services =>
        {
            var registration = services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(TService));
            services.Remove(registration);
        });

    public AuthAppFactory WithEnvironment(string environment)
    {
        _environment = environment;
        return this;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);

        // Every factory has a database of its own, hence a connection pool of its own that would stay open for
        // minutes after the test. Without this the suite runs out of server connections as it grows.
        await using var connection = new NpgsqlConnection(_settings["ConnectionStrings:Auth"]);
        NpgsqlConnection.ClearPool(connection);

        try
        {
            Directory.Delete(_manifestDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that cannot be removed is not a failure of the test.
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_environment is not null)
        {
            builder.UseEnvironment(_environment);
        }

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        if (_clock is { } clock)
        {
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(clock)));
        }

        foreach (var configure in _serviceOverrides)
        {
            builder.ConfigureTestServices(configure);
        }
    }
}
