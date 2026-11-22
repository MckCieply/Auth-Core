using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class ManifestOrgCreationTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string Roles = """
        permissions: [reports:read]
        default_roles:
          admin: ["*"]
          user: [reports:read]
        """;

    private static string UniqueDatabase() => "auth_" + Guid.NewGuid().ToString("N");

    private static Manifest Parsed(string yaml)
    {
        var result = ManifestParser.Parse(yaml);
        Assert.Null(result.Error);
        return Assert.IsType<Manifest>(result.Manifest);
    }

    private static ManifestState StateOf(AuthAppFactory factory) =>
        factory.Services.GetRequiredService<ManifestHolder>().State;

    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Auth-Core.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), relative);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\norg_creation:\n")]
    [InlineData("\norg_creation: ~\n")]
    [InlineData("\norg_creation: off\n")]
    [InlineData("\norg_creation: null\n")]
    [InlineData("\norg_creation: Null\n")]
    [InlineData("\norg_creation: NULL\n")]
    [InlineData("\norg_creation: 'off'\n")]
    public void The_key_is_off_when_absent_empty_valued_or_off(string tail)
    {
        Assert.False(Parsed(Roles + tail).OrgCreation);
    }

    [Fact]
    public void Self_service_turns_it_on()
    {
        Assert.True(Parsed(Roles + "\norg_creation: self-service\n").OrgCreation);
    }

    [Theory]
    [InlineData("''")]
    [InlineData("on")]
    [InlineData("yes")]
    [InlineData("Self-Service")]
    [InlineData("true")]
    [InlineData("'self-service '")]
    [InlineData("invite-only")]
    public void Any_other_value_is_invalid(string value)
    {
        var result = ManifestParser.Parse(Roles + "\norg_creation: " + value + "\n");

        Assert.Null(result.Manifest);
        Assert.Contains("org_creation", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stored_form_writes_the_key_only_when_on()
    {
        var off = Parsed(Roles);
        var on = Parsed(Roles + "\norg_creation: self-service\n");

        // The stored form of the same manifest before this change, byte for byte.
        Assert.Equal(
            """{"permissions":["reports:read"],"default_roles":[{"name":"admin","permissions":["*"]},{"name":"user","permissions":["reports:read"]}],"signup":"invite-only"}""",
            off.ToJson());
        Assert.DoesNotContain("org_creation", off.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"org_creation\":\"self-service\"", on.ToJson(), StringComparison.Ordinal);
        Assert.False(Manifest.FromJson(off.ToJson())!.OrgCreation);
        Assert.True(Manifest.FromJson(on.ToJson())!.OrgCreation);
        Assert.False(Manifest.FromJson("""{"permissions":["reports:read"],"default_roles":[{"name":"admin","permissions":["*"]}]}""")!.OrgCreation);
    }

    [Fact]
    public async Task A_manifest_with_the_key_is_activated_and_stored()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithOrgCreation();

        var state = StateOf(factory);

        Assert.False(state.IsDegraded);
        Assert.True(state.Manifest.OrgCreation);
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().ActiveManifests.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(Manifest.FromJson(stored.Content)!.OrgCreation);
    }

    [Fact]
    public async Task A_bad_value_keeps_the_last_valid_manifest_and_health_is_degraded()
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database).WithOrgCreation();
        _ = StateOf(first);

        await using var second = new AuthAppFactory(postgres, keys, database)
            .WithManifest(AuthAppFactory.DefaultManifest + "\norg_creation: sometimes\n");
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Contains("org_creation", state.DegradedReason, StringComparison.Ordinal);
        Assert.True(state.Manifest.OrgCreation);   // the last valid manifest stays active
        using var client = second.CreateClient();
        Assert.Equal("Degraded", await client.GetStringAsync("/auth/health", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_stored_manifest_from_an_earlier_version_reads_as_off()
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        _ = StateOf(first);
        const string stored = """{"permissions":["reports:read"],"default_roles":[{"name":"admin","permissions":["*"]}],"signup":"self-service"}""";
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await db.Database.ExecuteSqlAsync($"""UPDATE "ActiveManifests" SET "Content" = {stored}""", TestContext.Current.CancellationToken);
        }

        await using var second = new AuthAppFactory(postgres, keys, database).WithoutManifestFile();
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.False(state.Manifest.OrgCreation);
        Assert.Equal(SignupMode.SelfService, state.Manifest.Signup);
    }

    [Theory]
    [InlineData("deploy/auth.yaml")]
    [InlineData("samples/notes-api/auth.yaml")]
    public void The_shipped_development_manifests_turn_it_on(string relative)
    {
        var text = File.ReadAllText(RepositoryFile(relative));
        var result = ManifestParser.Parse(text);

        Assert.Null(result.Error);
        Assert.True(result.Manifest!.OrgCreation);
        Assert.Contains("# org_creation", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
    }
}
