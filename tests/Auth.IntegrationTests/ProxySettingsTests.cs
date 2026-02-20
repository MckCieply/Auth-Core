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

    [Theory]
    [InlineData(ProxySettings.KnownNetworksKey, "10.250.0.0")]       // no prefix length
    [InlineData(ProxySettings.KnownNetworksKey, "10.250.0.5/24")]    // bits set beyond the prefix: .NET 10 parses it and masks it to 10.250.0.0/24, we refuse it
    [InlineData(ProxySettings.KnownNetworksKey, "10.250.0.0/33")]
    [InlineData(ProxySettings.KnownNetworksKey, "not-a-network")]
    [InlineData(ProxySettings.KnownProxiesKey, "10.250.0.0/24")]     // a network where an address belongs
    [InlineData(ProxySettings.KnownProxiesKey, "proxy.internal")]    // a name, not an address
    [InlineData(ProxySettings.KnownProxiesKey, "300.1.1.1")]
    [InlineData(ProxySettings.KnownProxiesKey, "10.0.7")]            // the framework would read it as 10.0.0.7
    [InlineData(ProxySettings.KnownProxiesKey, "10")]                // ... as 0.0.0.10
    [InlineData(ProxySettings.KnownProxiesKey, "0x0a000007")]        // ... as 10.0.0.7
    [InlineData(ProxySettings.KnownProxiesKey, "010.0.0.7")]         // ... as 8.0.0.7 (octal)
    [InlineData(ProxySettings.KnownProxiesKey, "fe80::1%eth0")]      // a scope id would be dropped silently
    [InlineData(ProxySettings.KnownNetworksKey, "010.250.0.0/24")]   // octal: 8.250.0.0/24
    [InlineData(ProxySettings.KnownNetworksKey, "10.250/16")]        // 10.250.0.0/16 written short
    [InlineData(ProxySettings.KnownNetworksKey, "0.0.0.0/0")]        // every address: trusting everyone
    [InlineData(ProxySettings.KnownNetworksKey, "::/0")]
    [InlineData(ProxySettings.KnownProxiesKey, "::ffff:10.0.0.7")]          // an IPv4 proxy is written as IPv4
    [InlineData(ProxySettings.KnownNetworksKey, "::ffff:0.0.0.0/96")]       // every IPv4 client of a dual-stack listener
    [InlineData(ProxySettings.KnownNetworksKey, "::ffff:10.250.0.0/120")]
    [InlineData(ProxySettings.KnownNetworksKey, "::/64")]                   // a range that holds the whole mapped range
    [InlineData(ProxySettings.KnownNetworksKey, "::/80")]                   // the longest range that still holds all of it
    [InlineData(ProxySettings.KnownNetworksKey, "::ff00:0:0/88")]           // a range inside it, at the edge of the fixed bits
    public void A_bad_entry_stops_the_host_naming_the_key_and_not_the_value(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(new() { [key + ":0"] = value }));

        Assert.Contains($"'{key}'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData(" True ")]
    public void The_framework_switch_that_trusts_every_sender_stops_the_host_and_points_to_the_settings(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(new() { ["ForwardedHeaders_Enabled"] = value }));

        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ProxySettings.KnownNetworksKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains(ProxySettings.KnownProxiesKey, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("  ")]
    public void The_framework_switch_being_off_or_blank_is_no_reason_to_stop(string value) =>
        Assert.True(Load(new() { ["ForwardedHeaders_Enabled"] = value }).IsEmpty);

    [Fact]
    public void Plain_ipv6_ranges_and_addresses_in_their_usual_spelling_are_accepted()
    {
        var settings = Load(new()
        {
            [ProxySettings.KnownNetworksKey] = "2001:db8::/32",
            [ProxySettings.KnownProxiesKey] = "::1",
        });

        Assert.Equal(["2001:db8::/32"], settings.Networks.Select(n => n.ToString()));
        Assert.Equal([IPAddress.IPv6Loopback], settings.Proxies);
    }

    [Theory]
    [InlineData("::1/128")]   // beside the mapped range, not in it
    [InlineData("::/81")]     // the first range that stops short of the bits that make an address mapped
    public void A_range_next_to_the_mapped_range_is_accepted(string network)
    {
        var settings = Load(new() { [ProxySettings.KnownNetworksKey] = network });

        Assert.Equal([network], settings.Networks.Select(n => n.ToString()));
    }
}
