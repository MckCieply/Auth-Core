using Auth.Server.Audit;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class AuditSettingsTests
{
    private static AuditSettings Load(Dictionary<string, string?> values) =>
        AuditSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void The_retention_is_90_days_by_default()
    {
        Assert.Equal(90, Load([]).RetentionDays);
        Assert.Equal("Auth:Audit:RetentionDays", AuditSettings.RetentionDaysKey);
    }

    [Fact]
    public void The_retention_is_a_setting()
    {
        Assert.Equal(7, Load(new() { [AuditSettings.RetentionDaysKey] = "7" }).RetentionDays);
        Assert.Equal(1, Load(new() { [AuditSettings.RetentionDaysKey] = "1" }).RetentionDays);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_value_means_the_default(string? blank) =>
        Assert.Equal(90, Load(new() { [AuditSettings.RetentionDaysKey] = blank }).RetentionDays);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    public void A_value_that_is_not_a_whole_number_of_at_least_one_stops_the_host_naming_the_key(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(new() { [AuditSettings.RetentionDaysKey] = value }));

        Assert.Contains($"'{AuditSettings.RetentionDaysKey}'", ex.Message, StringComparison.Ordinal);
    }
}
