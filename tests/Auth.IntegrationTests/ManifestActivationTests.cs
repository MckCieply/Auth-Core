using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

public sealed class ManifestActivationTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string Broken = "default_roles:\n  user: [reports:write]\n";

    private const string Other = """
        permissions: [orders:read]
        default_roles:
          owner: ["*"]
        """;

    private static string UniqueDatabase() => "auth_" + Guid.NewGuid().ToString("N");

    private static ManifestState StateOf(AuthAppFactory factory) =>
        factory.Services.GetRequiredService<ManifestHolder>().State;

    private static async Task<string> HealthAsync(AuthAppFactory factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/auth/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);   // Degraded is still a 200
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<ActiveManifest?> StoredAsync(AuthAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().ActiveManifests.AsNoTracking()
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Valid_manifest_becomes_active_and_is_stored()   // criterion 20
    {
        await using var factory = new AuthAppFactory(postgres, keys);

        var state = StateOf(factory);

        Assert.False(state.IsDegraded);
        Assert.Equal(["reports:read", "reports:approve", "templates:manage"], state.Manifest.Permissions);
        Assert.Equal("Healthy", await HealthAsync(factory));
        var stored = Assert.IsType<ActiveManifest>(await StoredAsync(factory));
        Assert.Equal(state.Manifest.ToJson(), stored.Content);
    }

    [Fact]
    public async Task A_new_valid_manifest_replaces_the_stored_one_at_the_next_start()   // criterion 21
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        _ = StateOf(first);

        await using var second = new AuthAppFactory(postgres, keys, database).WithManifest(Other);
        var state = StateOf(second);

        Assert.False(state.IsDegraded);
        Assert.Equal(["orders:read"], state.Manifest.Permissions);
        Assert.Equal(state.Manifest.ToJson(), (await StoredAsync(second))!.Content);
        using var scope = second.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AuthDbContext>().ActiveManifests.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Invalid_manifest_leaves_the_last_stored_one_active_and_says_why()   // criterion 20
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        var good = StateOf(first).Manifest;
        var logs = new CapturingLoggerProvider();

        await using var second = new AuthAppFactory(postgres, keys, database)
            .WithManifest(Broken)
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));
        var state = StateOf(second);   // the host starts

        Assert.True(state.IsDegraded);
        Assert.Equal(good.ToJson(), state.Manifest.ToJson());
        Assert.Equal("Degraded", await HealthAsync(second));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("reports:write") && e.Message.Contains("last valid stored"));
        Assert.Equal(good.ToJson(), (await StoredAsync(second))!.Content);   // the broken file is not stored
    }

    [Fact]
    public async Task Missing_manifest_file_leaves_the_last_stored_one_active()   // criterion 20
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        var good = StateOf(first).Manifest;

        await using var second = new AuthAppFactory(postgres, keys, database).WithoutManifestFile();
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Contains("does not exist", state.DegradedReason);
        Assert.Equal(good.ToJson(), state.Manifest.ToJson());
        Assert.Equal("Degraded", await HealthAsync(second));
    }

    [Fact]
    public async Task Unreadable_manifest_path_leaves_the_last_stored_one_active()   // criterion 20
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        var good = StateOf(first).Manifest;

        // A directory where the file should be: nothing can be read from it.
        await using var second = new AuthAppFactory(postgres, keys, database).WithSetting(ManifestSettings.PathKey, Path.GetTempPath());
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Equal(good.ToJson(), state.Manifest.ToJson());
    }

    [Fact]
    public async Task Manifest_file_over_the_size_limit_is_not_read()
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + "\n# " + new string('x', ManifestParser.MaxBytes));

        var state = StateOf(factory);

        Assert.True(state.IsDegraded);
        Assert.Contains("larger than", state.DegradedReason);
    }

    [Fact]
    public async Task A_manifest_with_more_than_100_default_roles_leaves_the_last_stored_one_active()   // criterion 20
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        var good = StateOf(first).Manifest;
        var tooMany = "default_roles:\n  admin: [\"*\"]\n" + string.Concat(Enumerable.Range(1, 100).Select(i => $"  r{i}: []\n"));   // 101 roles

        await using var second = new AuthAppFactory(postgres, keys, database).WithManifest(tooMany);
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Contains("100", state.DegradedReason);
        Assert.Equal(good.ToJson(), state.Manifest.ToJson());
    }

    [Fact]
    public async Task With_nothing_ever_stored_the_catalog_is_the_built_in_one_and_the_only_default_role_is_admin()   // criterion 20
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithManifest(Broken);

        var state = StateOf(factory);

        Assert.True(state.IsDegraded);
        Assert.Equal(PermissionCatalog.BuiltIn, state.Manifest.Catalog.Permissions);
        var role = Assert.Single(state.Manifest.DefaultRoles);
        Assert.Equal("admin", role.Name);
        Assert.Equal(["*"], role.Permissions);
        Assert.Equal("Degraded", await HealthAsync(factory));
        Assert.Null(await StoredAsync(factory));
    }

    [Fact]
    public async Task A_stored_manifest_that_cannot_be_read_back_is_replaced_by_the_built_in_one()
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        _ = StateOf(first);
        using (var scope = first.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database
                .ExecuteSqlAsync($"""UPDATE "ActiveManifests" SET "Content" = 'not json'""", TestContext.Current.CancellationToken);
        }

        await using var second = new AuthAppFactory(postgres, keys, database).WithManifest(Broken);
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Equal(Manifest.BuiltIn.ToJson(), state.Manifest.ToJson());
    }

    [Fact]
    public async Task Host_refuses_to_start_without_a_manifest_path()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting(ManifestSettings.PathKey, "");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(ManifestSettings.PathKey, ex.ToString());
    }

    [Fact]
    public async Task Shipped_development_manifest_is_valid()
    {
        // The path development uses: relative to the content root of the server project.
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting(ManifestSettings.PathKey, "../../deploy/auth.yaml");

        var state = StateOf(factory);

        Assert.False(state.IsDegraded, state.DegradedReason);
        Assert.Equal(["documents:read", "documents:write", "documents:approve"], state.Manifest.Permissions);
        Assert.Equal(["admin", "user"], state.Manifest.DefaultRoles.Select(r => r.Name));
    }
}
