using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
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
using Microsoft.EntityFrameworkCore;
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
    private readonly string? _dropConnectionString;

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
        if (database is null)
        {
            _dropConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionStringFor("postgres")) { Pooling = false, Timeout = 30 }.ConnectionString;
        }

        // The default 15 s to connect and 30 s per command are too short when the shared server is busy (a big run, other sessions on the
        // same Docker): a test then fails with a timeout that says nothing about the code under test.
        _settings["ConnectionStrings:Auth"] = new NpgsqlConnectionStringBuilder(postgres.ConnectionStringFor(DatabaseName))
        {
            Timeout = 30,
            CommandTimeout = 60,
        }.ConnectionString;
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
        // Blank, like the three above: the Development settings file the test host reads would otherwise turn the address on in every
        // test, and a variable of the machine would change what a test sees. A test that wants sign-up on uses WithSignup().
        _settings[MailSettingsLoader.CompleteSignupUrlKey] = "";
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
        // A variable of the machine cannot change how long a test host keeps its audit rows.
        _settings[AuditSettings.RetentionDaysKey] = "";
        // The list form of a variable of the machine (Auth__Proxy__KnownNetworks__0) adds children that the two keys above do not
        // cover: every such variable is pinned to blank as well.
        foreach (var (key, value) in ProxyEnvironment.PinnedBlankFromProcess())
        {
            _settings[key] = value;
        }
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

    /// <summary>The default manifest with <c>signup: self-service</c>.</summary>
    public const string SelfServiceManifest = DefaultManifest + "\nsignup: self-service\n";

    /// <summary>The block that turns private sign-up on (spec 0010), with two permissions of the default catalog for the private role.</summary>
    public const string PrivateBlock = "\nprivate:\n  signup: self-service\n  permissions: [reports:read, reports:approve]\n";

    public const string DefaultAppName = "Auth-Core Test";
    public const string DefaultResetUrl = "https://app.example.com/reset";
    public const string DefaultVerifyUrl = "https://app.example.com/verify";
    public const string DefaultInviteUrl = "https://app.example.com/invite";
    public const string DefaultCompleteSignupUrl = "https://app.example.com/complete-signup";
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

    /// <summary>Turns sign-up on: the manifest says <c>signup: self-service</c> and the completion address is set. Call it before the host is built.</summary>
    public AuthAppFactory WithSignup()
    {
        WithManifest(SelfServiceManifest);
        return WithSetting(MailSettingsLoader.CompleteSignupUrlKey, DefaultCompleteSignupUrl);
    }

    /// <summary>
    /// Turns private sign-up on (spec 0010) and, unless told otherwise, organization sign-up too: the manifest and the completion address.
    /// Call it before the host is built.
    /// </summary>
    public AuthAppFactory WithPrivateSignup(bool organization = true)
    {
        WithManifest((organization ? SelfServiceManifest : DefaultManifest) + PrivateBlock);
        return WithSetting(MailSettingsLoader.CompleteSignupUrlKey, DefaultCompleteSignupUrl);
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
        await using (var connection = new NpgsqlConnection(_settings["ConnectionStrings:Auth"]))
        {
            NpgsqlConnection.ClearPool(connection);
        }

        if (_dropConnectionString is not null)
        {
            DropDatabase(_dropConnectionString);
        }

        try
        {
            Directory.Delete(_manifestDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that cannot be removed is not a failure of the test.
        }
    }

    /// <summary>
    /// Drops the database this factory created for itself, so the server does not keep one database per test of the run. Only a database
    /// the factory named itself is dropped: one that a test named is shared by several factories (restart scenarios) and belongs to the
    /// test. Connections still open to it are terminated by the drop. A drop that fails leaves the database behind and fails nothing.
    /// </summary>
    private void DropDatabase(string maintenanceConnectionString)
    {
        try
        {
            using var connection = new NpgsqlConnection(maintenanceConnectionString);
            connection.Open();
            using var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS \"" + DatabaseName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" WITH (FORCE)", connection);
            drop.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException or IOException)
        {
            // A database that cannot be dropped now is not a failure of the test that has already ended.
        }
    }

    /// <summary>
    /// Creates this host's database and migrates it, by a context of its own that logs nothing, if the server does not have the database
    /// yet; a database that is there is left as it is (the restart and upgrade tests start hosts on a database in a state of their own). The
    /// host's start-up migration then finds nothing to do. Left to itself it probes a database that is not there, and then a history table
    /// that is not there, and EF logs each failed probe as an error line in every test. A failure of the creation or of a migration is not
    /// caught: it fails the test that started the host. A test that blanks or breaks the connection string, or turns the start-up migration
    /// off, gets the host as it is: the host reports its own error.
    /// </summary>
    private void EnsureDatabase()
    {
        if (_settings.GetValueOrDefault("Auth:Database:MigrateOnStartup") != "true"
            || string.IsNullOrWhiteSpace(_settings.GetValueOrDefault("ConnectionStrings:Auth")))
        {
            return;
        }

        var connectionString = _settings["ConnectionStrings:Auth"]!;
        NpgsqlConnectionStringBuilder maintenance;
        try
        {
            maintenance = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return;   // not a connection string: the host fails on it, as it would without this step
        }

        var database = maintenance.Database;
        if (string.IsNullOrWhiteSpace(database))
        {
            return;
        }

        maintenance.Database = "postgres";
        maintenance.Pooling = false;
        using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        connection.Open();
        using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", database);
        if (exists.ExecuteScalar() is not null)
        {
            return;
        }

        // A database name cannot be a parameter: quote it as an identifier (the name is generated or chosen by the test).
        using (var create = new NpgsqlCommand("CREATE DATABASE \"" + database.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"", connection))
        {
            create.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connectionString).UseOpenIddict().Options;
        using var db = new AuthDbContext(options);
        db.Database.Migrate();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        EnsureDatabase();

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
