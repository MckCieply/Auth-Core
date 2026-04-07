using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Signup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

public sealed class SignupGateTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static SignupGate GateOf(AuthAppFactory factory) => factory.Services.GetRequiredService<SignupGate>();

    private static async Task<string> HealthAsync(AuthAppFactory factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/auth/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static bool WarnsAboutTheAddress((LogLevel Level, string Category, string Message) entry) =>
        entry.Level == LogLevel.Warning && entry.Message.Contains(MailSettingsLoader.CompleteSignupUrlKey, StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(SignupVariants.Off), MemberType = typeof(SignupVariants))]
    public async Task Sign_up_is_off_unless_the_manifest_says_self_service_and_the_address_is_set(string variant)   // criteria 1, 2
    {
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), variant);

        var gate = GateOf(factory);

        Assert.False(gate.IsOn);
        Assert.Equal("invite-only", gate.ModeName);
    }

    [Fact]
    public async Task With_the_key_and_the_address_sign_up_is_on()   // criterion 1
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();

        var gate = GateOf(factory);

        Assert.True(gate.IsOn);
        Assert.False(gate.MissingAddress);
        Assert.Equal("self-service", gate.ModeName);
        Assert.Equal("Healthy", await HealthAsync(factory));
    }

    [Fact]
    public async Task Self_service_without_the_address_warns_at_the_start_and_health_says_degraded()   // criterion 17
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), SignupVariants.SelfServiceWithoutAddress)
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        var gate = GateOf(factory);

        Assert.False(gate.IsOn);
        Assert.True(gate.MissingAddress);
        Assert.Equal("Degraded", await HealthAsync(factory));
        var warning = Assert.Single(logs.Entries, WarnsAboutTheAddress);
        Assert.Contains("sign-up is off", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SignupVariants.NoKey)]
    [InlineData(SignupVariants.NoKeyWithAddress)]
    [InlineData(SignupVariants.InviteOnlyWithAddress)]
    public async Task The_other_ways_to_be_off_are_healthy_and_log_no_warning(string variant)   // criterion 2: nothing changes for an instance that does not opt in
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), variant)
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Equal("Healthy", await HealthAsync(factory));
        Assert.DoesNotContain(logs.Entries, WarnsAboutTheAddress);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/complete-signup")]
    [InlineData("https://app.example.com/complete-signup#top")]
    [InlineData("https://user:secret@app.example.com/complete-signup")]
    [InlineData("https://app.example.com/complete-signup?token=abc")]
    public async Task A_bad_address_stops_the_start_naming_the_key_and_not_the_value(string value)   // criterion 17
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup()
            .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, value);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(MailSettingsLoader.CompleteSignupUrlKey, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.ToString(), StringComparison.Ordinal);
    }
}
