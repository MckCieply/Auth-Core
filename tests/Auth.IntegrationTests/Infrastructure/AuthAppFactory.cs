using Auth.Infrastructure.Identity;
using Auth.Server.Seeding;
using TokenOptions = Auth.Server.Tokens.TokenOptions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Auth.IntegrationTests.Infrastructure;

public class AuthAppFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings = new();
    private string? _environment;
    private TimeProvider? _clock;
    private readonly List<Action<IServiceCollection>> _serviceOverrides = [];

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
    }

    /// <summary>Default development seed credentials; the password satisfies Identity's default policy.</summary>
    public const string DefaultSeedEmail = "user@example.com";

    public const string DefaultSeedPassword = "Correct-Horse-Battery-1";

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
