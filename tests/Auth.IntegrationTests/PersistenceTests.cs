using System.Net;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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
    public async Task Second_user_with_the_same_email_in_a_different_case_is_rejected()
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True((await users.CreateAsync(new ApplicationUser { UserName = "first", Email = "dup@example.com" }, "Correct-Horse-1")).Succeeded);

        var result = await users.CreateAsync(new ApplicationUser { UserName = "second", Email = "DUP@Example.com" }, "Correct-Horse-1");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == nameof(IdentityErrorDescriber.DuplicateEmail));
    }

    [Fact]
    public async Task A_host_started_on_an_empty_database_migrates_it_and_answers_health()
    {
        // The test host's own set-up leaves an existing database alone, so this start is the service's start-up migration on a database
        // that has none of its tables.
        var database = "auth_" + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnectionStringBuilder(postgres.ConnectionStringFor("postgres")).ConnectionString;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var factory = new AuthAppFactory(postgres, keys, database);
        await using (var empty = new NpgsqlConnection(factory.ConnectionString))
        {
            await empty.OpenAsync(TestContext.Current.CancellationToken);
            await using var tables = new NpgsqlCommand("SELECT to_regclass('\"Companies\"') IS NULL", empty);
            Assert.True((bool)(await tables.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);   // the precondition
        }

        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        using var health = await client.GetAsync("/auth/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(factory.SeedEmail));   // the seed ran after the schema
    }

    [Fact]
    public async Task Host_fails_fast_without_connection_string()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting("ConnectionStrings:Auth", "");
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ConnectionStrings:Auth", ex.ToString());
    }
}
