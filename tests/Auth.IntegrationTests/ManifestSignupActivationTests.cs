using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class ManifestSignupActivationTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
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

    [Fact]
    public async Task A_self_service_manifest_becomes_active_and_is_stored_with_its_mode()   // criterion 16
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();

        var state = StateOf(factory);

        Assert.False(state.IsDegraded);
        Assert.Equal(SignupMode.SelfService, state.Manifest.Signup);
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().ActiveManifests.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SignupMode.SelfService, Manifest.FromJson(stored.Content)!.Signup);
    }

    [Fact]
    public async Task A_refused_value_keeps_the_last_valid_manifest_and_health_says_degraded()   // criterion 16
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database).WithSignup();
        _ = StateOf(first);

        await using var second = new AuthAppFactory(postgres, keys, database)
            .WithManifest(AuthAppFactory.DefaultManifest + "\nsignup: sometimes\n");
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Contains("signup", state.DegradedReason, StringComparison.Ordinal);
        Assert.Equal(SignupMode.SelfService, state.Manifest.Signup);   // the last valid manifest stays active
        Assert.Equal("Degraded", await HealthAsync(second));
    }

    [Fact]
    public async Task A_manifest_stored_by_v0_1_0_stays_invite_only_when_the_file_is_missing()   // criterion 16, Review Focus 1
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        _ = StateOf(first);
        // What v0.1.0 stored: the same JSON without the new member.
        const string stored = """{"permissions":["reports:read"],"default_roles":[{"name":"admin","permissions":["*"]}]}""";
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await db.Database.ExecuteSqlAsync($"""UPDATE "ActiveManifests" SET "Content" = {stored}""", TestContext.Current.CancellationToken);
        }

        await using var second = new AuthAppFactory(postgres, keys, database).WithoutManifestFile();
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Equal(SignupMode.InviteOnly, state.Manifest.Signup);
        Assert.Equal(["reports:read"], state.Manifest.Permissions);
    }
}
