using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public class PersistenceTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    [Fact]
    public async Task Created_user_is_found_by_email_after_migrations()
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await users.CreateAsync(new ApplicationUser { UserName = "p@example.com", Email = "p@example.com" }, "Correct-Horse-1");
        Assert.True(result.Succeeded);
        Assert.NotNull(await users.FindByEmailAsync("p@example.com"));
    }

    [Fact]
    public async Task Host_fails_fast_without_connection_string()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting("ConnectionStrings:Auth", "");
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ConnectionStrings:Auth", ex.ToString());
    }
}
