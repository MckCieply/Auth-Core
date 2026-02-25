using Auth.Server.RateLimiting;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class RateLimitSettingsTests
{
    private static RateLimitSettings Load(Dictionary<string, string?> values) =>
        RateLimitSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void The_defaults_are_the_numbers_of_the_contract()
    {
        var settings = Load([]);

        Assert.True(settings.Enabled);
        Assert.Equal(30, settings.PermitPerMinute(RatePolicy.Login));
        Assert.Equal(60, settings.PermitPerMinute(RatePolicy.Refresh));
        Assert.Equal(10, settings.PermitPerMinute(RatePolicy.Email));
        Assert.Equal(20, settings.PermitPerMinute(RatePolicy.Invite));
        Assert.Equal(300, settings.PermitPerMinute(RatePolicy.General));
    }

    [Fact]
    public void The_keys_are_named_by_policy()
    {
        Assert.Equal("Auth:RateLimit:Login:PermitPerMinute", RateLimitSettings.PermitKey(RatePolicy.Login));
        Assert.Equal("Auth:RateLimit:Refresh:PermitPerMinute", RateLimitSettings.PermitKey(RatePolicy.Refresh));
        Assert.Equal("Auth:RateLimit:Email:PermitPerMinute", RateLimitSettings.PermitKey(RatePolicy.Email));
        Assert.Equal("Auth:RateLimit:Invite:PermitPerMinute", RateLimitSettings.PermitKey(RatePolicy.Invite));
        Assert.Equal("Auth:RateLimit:General:PermitPerMinute", RateLimitSettings.PermitKey(RatePolicy.General));
        Assert.Equal("Auth:RateLimit:Enabled", RateLimitSettings.EnabledKey);
    }

    [Fact]
    public void A_policy_is_set_on_its_own()
    {
        var settings = Load(new() { [RateLimitSettings.PermitKey(RatePolicy.Email)] = "25", [RateLimitSettings.EnabledKey] = "false" });

        Assert.False(settings.Enabled);
        Assert.Equal(25, settings.PermitPerMinute(RatePolicy.Email));
        Assert.Equal(30, settings.PermitPerMinute(RatePolicy.Login));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_value_means_the_default(string? blank)   // compose passes an empty string for a variable that is not set
    {
        var settings = Load(new()
        {
            [RateLimitSettings.EnabledKey] = blank,
            [RateLimitSettings.PermitKey(RatePolicy.Login)] = blank,
        });

        Assert.True(settings.Enabled);
        Assert.Equal(30, settings.PermitPerMinute(RatePolicy.Login));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("99999999999")]
    public void A_permit_that_is_not_a_whole_number_of_at_least_one_stops_the_host_naming_the_key(string value)
    {
        var key = RateLimitSettings.PermitKey(RatePolicy.Refresh);

        var ex = Assert.Throws<InvalidOperationException>(() => Load(new() { [key] = value }));

        Assert.Contains($"'{key}'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TRUE", true)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    public void Enabled_reads_true_and_false_in_any_case(string value, bool expected) =>
        Assert.Equal(expected, Load(new() { [RateLimitSettings.EnabledKey] = value }).Enabled);

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("off")]
    public void Enabled_that_is_not_true_or_false_stops_the_host_naming_the_key(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(new() { [RateLimitSettings.EnabledKey] = value }));

        Assert.Contains($"'{RateLimitSettings.EnabledKey}'", ex.Message, StringComparison.Ordinal);
    }
}
