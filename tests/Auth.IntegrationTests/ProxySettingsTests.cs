using System.Net;
using Auth.Server.Network;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class ProxySettingsTests
{
    private static ProxySettings Load(Dictionary<string, string?> values) =>
        ProxySettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void Nothing_is_trusted_by_default()   // criterion 2
    {
        var settings = Load([]);

        Assert.True(settings.IsEmpty);
        Assert.Empty(settings.Networks);
        Assert.Empty(settings.Proxies);
        Assert.True(ProxySettings.None.IsEmpty);
    }

    [Fact]
    public void Blank_entries_count_as_unset()   // what compose passes for a variable that is not set
    {
        var settings = Load(new()
        {
            [ProxySettings.KnownNetworksKey + ":0"] = "",
            [ProxySettings.KnownProxiesKey] = "  ",
        });

        Assert.True(settings.IsEmpty);
    }

    [Fact]
    public void Networks_and_proxies_are_read_as_lists()
    {
        var settings = Load(new()
        {
            [ProxySettings.KnownNetworksKey + ":0"] = "10.250.0.0/24",
            [ProxySettings.KnownNetworksKey + ":1"] = "fd00::/8",
            [ProxySettings.KnownProxiesKey + ":0"] = "10.0.0.7",
            [ProxySettings.KnownProxiesKey + ":1"] = "2001:db8::1",
        });

        Assert.False(settings.IsEmpty);
        Assert.Equal(["10.250.0.0/24", "fd00::/8"], settings.Networks.Select(n => n.ToString()));
        Assert.Equal(["10.0.0.7", "2001:db8::1"], settings.Proxies.Select(p => p.ToString()));
    }

    [Fact]
    public void One_value_may_hold_several_entries_separated_by_commas()   // a .env file has one line per variable
    {
        var settings = Load(new()
        {
            [ProxySettings.KnownNetworksKey] = "10.250.0.0/24, 10.250.1.0/24",
            [ProxySettings.KnownProxiesKey] = "10.0.0.7,10.0.0.8",
        });

        Assert.Equal(2, settings.Networks.Count);
        Assert.Equal(2, settings.Proxies.Count);
    }

    [Fact]
    public void A_proxy_written_as_an_ipv4_mapped_address_is_the_ipv4_address()   // a plain remote would not match the mapped form
    {
        var settings = Load(new() { [ProxySettings.KnownProxiesKey + ":0"] = "::ffff:10.0.0.7" });

        Assert.Equal([IPAddress.Parse("10.0.0.7")], settings.Proxies);
    }

    [Theory]
    [InlineData(ProxySettings.KnownNetworksKey, "10.250.0.0")]       // no prefix length
    [InlineData(ProxySettings.KnownNetworksKey, "10.250.0.5/24")]    // bits set beyond the prefix: .NET 10 parses it and masks it to 10.250.0.0/24, we refuse it
    [InlineData(ProxySettings.KnownNetworksKey, "10.250.0.0/33")]
    [InlineData(ProxySettings.KnownNetworksKey, "not-a-network")]
    [InlineData(ProxySettings.KnownProxiesKey, "10.250.0.0/24")]     // a network where an address belongs
    [InlineData(ProxySettings.KnownProxiesKey, "proxy.internal")]    // a name, not an address
    [InlineData(ProxySettings.KnownProxiesKey, "300.1.1.1")]
    public void A_bad_entry_stops_the_host_naming_the_key_and_not_the_value(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(new() { [key + ":0"] = value }));

        Assert.Contains($"'{key}'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
    }
}
