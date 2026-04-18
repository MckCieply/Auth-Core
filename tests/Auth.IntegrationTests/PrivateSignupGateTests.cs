using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Signup;
using Auth.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → General rules and <c>GET /auth/options</c>: two independent switches.</summary>
public sealed class PrivateSignupGateTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static async Task<string> BodyAsync(AuthAppFactory factory, string path)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Theory]
    [InlineData(false, false, """{"signup":"invite-only","private_signup":"off"}""")]
    [InlineData(true, false, """{"signup":"self-service","private_signup":"off"}""")]
    [InlineData(false, true, """{"signup":"invite-only","private_signup":"self-service"}""")]
    [InlineData(true, true, """{"signup":"self-service","private_signup":"self-service"}""")]
    public async Task The_four_combinations_of_the_two_switches_give_their_own_options(bool organization, bool @private, string expected)   // criterion 2
    {
        var manifest = (organization ? AuthAppFactory.SelfServiceManifest : AuthAppFactory.DefaultManifest) + (@private ? AuthAppFactory.PrivateBlock : "");
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(manifest)
            .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, AuthAppFactory.DefaultCompleteSignupUrl);

        Assert.Equal(expected, await BodyAsync(factory, "/auth/options"));
        var gate = factory.Services.GetRequiredService<SignupGate>();
        Assert.Equal(organization, gate.IsOn);
        Assert.Equal(@private, gate.PrivateOn);
        Assert.Equal(organization || @private, gate.AnyOn);
        Assert.Equal(organization, gate.IsOnFor(SignupKind.Organization));
        Assert.Equal(@private, gate.IsOnFor(SignupKind.Private));
        Assert.Equal("Healthy", await BodyAsync(factory, "/auth/health"));
    }

    [Fact]
    public async Task Private_sign_up_without_the_address_is_off_warns_at_the_start_and_health_says_degraded()   // criterion 2
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.PrivateBlock)
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Equal("""{"signup":"invite-only","private_signup":"off"}""", await BodyAsync(factory, "/auth/options"));
        Assert.Equal("Degraded", await BodyAsync(factory, "/auth/health"));
        var warning = Assert.Single(
            logs.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains(MailSettingsLoader.CompleteSignupUrlKey, StringComparison.Ordinal));
        Assert.Contains("private sign-up is off", warning.Message, StringComparison.Ordinal);
        Assert.True(factory.Services.GetRequiredService<SignupGate>().MissingAddress);
    }

    [Fact]
    public async Task Both_switches_wanted_without_the_address_warn_once_each()   // criterion 2
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.SelfServiceManifest + AuthAppFactory.PrivateBlock)
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Equal("""{"signup":"invite-only","private_signup":"off"}""", await BodyAsync(factory, "/auth/options"));
        Assert.Equal("Degraded", await BodyAsync(factory, "/auth/health"));
        var warnings = logs.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains(MailSettingsLoader.CompleteSignupUrlKey, StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Single(warnings, e => e.Message.Contains("private sign-up is off", StringComparison.Ordinal));
        Assert.Single(warnings, e => !e.Message.Contains("private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_manifest_that_breaks_the_block_keeps_the_last_valid_one_and_health_says_degraded()   // criterion 1
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        await using (var first = new AuthAppFactory(postgres, keys, database).WithPrivateSignup())
        {
            Assert.True(first.Services.GetRequiredService<ManifestHolder>().Current.PrivateSignup);
        }

        await using var second = new AuthAppFactory(postgres, keys, database)
            .WithManifest(AuthAppFactory.SelfServiceManifest + "\nprivate:\n  signup: self-service\n  permissions: [members:manage]\n")
            .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, AuthAppFactory.DefaultCompleteSignupUrl);

        var state = second.Services.GetRequiredService<ManifestHolder>().State;
        Assert.True(state.IsDegraded);
        Assert.True(state.Manifest.PrivateSignup);   // the last valid one, as stored by the first start
        Assert.Equal(["reports:approve", "reports:read"], state.Manifest.PrivatePermissions);
        Assert.Equal("Degraded", await BodyAsync(second, "/auth/health"));
    }
}
