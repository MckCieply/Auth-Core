using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Auth.IntegrationTests.Infrastructure;

public class AuthAppFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings = new();
    private string? _environment;

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
    }

    public string DatabaseName { get; }

    public AuthAppFactory WithSetting(string key, string? value)
    {
        _settings[key] = value;
        return this;
    }

    public AuthAppFactory WithEnvironment(string environment)
    {
        _environment = environment;
        return this;
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
    }
}
