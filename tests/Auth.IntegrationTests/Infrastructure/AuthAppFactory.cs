using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Auth.IntegrationTests.Infrastructure;

public class AuthAppFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings = new();
    private string? _environment;

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
