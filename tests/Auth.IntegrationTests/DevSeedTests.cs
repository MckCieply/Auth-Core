using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public class DevSeedTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static string UniqueDatabase() => "auth_" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Development_host_seeds_the_configured_user_once()
    {
        var database = UniqueDatabase();
        await using var a = new AuthAppFactory(postgres, keys, database);
        _ = a.CreateClient();
        await using var b = new AuthAppFactory(postgres, keys, database); // second start = restart
        _ = b.CreateClient();

        using var scope = b.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var normalizedEmail = users.NormalizeEmail(b.SeedEmail);
        Assert.Equal(1, await db.Users.CountAsync(u => u.NormalizedEmail == normalizedEmail));

        var user = await users.FindByEmailAsync(b.SeedEmail);
        Assert.NotNull(user);
        Assert.True(user.EmailConfirmed);
        Assert.True(await users.CheckPasswordAsync(user, b.SeedPassword));
    }

    [Fact]
    public async Task Development_seed_does_not_reset_an_existing_password()
    {
        var database = UniqueDatabase();
        await using var a = new AuthAppFactory(postgres, keys, database);
        _ = a.CreateClient();
        await using var b = new AuthAppFactory(postgres, keys, database)
            .WithSetting("Auth:DevSeed:Password", "Another-Password-2");
        _ = b.CreateClient();

        using var scope = b.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(a.SeedEmail);
        Assert.NotNull(user);
        Assert.True(await users.CheckPasswordAsync(user, a.SeedPassword));
        Assert.False(await users.CheckPasswordAsync(user, "Another-Password-2"));
    }

    [Fact]
    public async Task Production_host_never_seeds()
    {
        await using var f = new AuthAppFactory(postgres, keys)
            .WithEnvironment("Production")
            .WithSetting("Auth:DevSeed:Email", "prod-seed@example.com");
        _ = f.CreateClient();

        using var scope = f.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync("prod-seed@example.com"));
    }

    [Theory]
    [InlineData("Auth:Tokens:Issuer")]
    [InlineData("Auth:Tokens:Audience")]
    public async Task Production_host_refuses_to_start_on_default_token_identifiers(string key)
    {
        // Key absent from configuration: the class defaults (the development identifiers) would silently apply.
        await using var f = new AuthAppFactory(postgres, keys)
            .WithEnvironment("Production")
            .WithoutSetting(key);

        // An Auth__Tokens__* variable on this machine would fill the gap: hide it while the host is built. Every other
        // test pins both identifiers through settings, which win over the environment, so they are unaffected.
        var variable = key.Replace(":", "__", StringComparison.Ordinal);
        var saved = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, null);
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
            Assert.Contains(key, ex.ToString());
            Assert.Contains("explicitly", ex.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, saved);
        }
    }

    [Fact]
    public async Task Development_host_starts_on_the_default_token_identifiers()
    {
        await using var f = new AuthAppFactory(postgres, keys)
            .WithoutSetting("Auth:Tokens:Issuer")
            .WithoutSetting("Auth:Tokens:Audience");

        using var client = f.CreateClient();
        using var response = await client.GetAsync("/auth/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
