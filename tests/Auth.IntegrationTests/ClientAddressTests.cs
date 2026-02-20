using System.Net;
using Auth.Server.Network;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

public sealed class ClientAddressTests
{
    private static DefaultHttpContext With(string? remote)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        return context;
    }

    [Fact]
    public void An_ipv4_address_is_its_own_partition()
    {
        var context = With("203.0.113.9");

        Assert.Equal("203.0.113.9", ClientAddress.Text(context));
        Assert.Equal("203.0.113.9", ClientAddress.PartitionOf(context));
    }

    [Fact]
    public void An_ipv4_address_that_reaches_kestrel_mapped_into_ipv6_is_the_ipv4_address()
    {
        var context = With("::ffff:203.0.113.9");

        Assert.Equal(IPAddress.Parse("203.0.113.9"), ClientAddress.Of(context));
        Assert.Equal("203.0.113.9", ClientAddress.Text(context));
        Assert.Equal("203.0.113.9", ClientAddress.PartitionOf(context));
    }

    [Fact]
    public void An_ipv6_address_is_counted_by_its_64_bit_network_and_recorded_in_full()
    {
        var one = With("2001:db8:1:2::1");
        var other = With("2001:db8:1:2:ffff:ffff:ffff:ffff");
        var elsewhere = With("2001:db8:1:3::1");

        Assert.Equal("2001:db8:1:2::/64", ClientAddress.PartitionOf(one));
        Assert.Equal(ClientAddress.PartitionOf(one), ClientAddress.PartitionOf(other));
        Assert.NotEqual(ClientAddress.PartitionOf(one), ClientAddress.PartitionOf(elsewhere));
        Assert.Equal("2001:db8:1:2::1", ClientAddress.Text(one));   // the audit log keeps the whole address
    }

    [Fact]
    public void A_request_without_an_address_is_unknown()   // the test host has none
    {
        var context = With(null);

        Assert.Null(ClientAddress.Of(context));
        Assert.Equal("unknown", ClientAddress.Text(context));
        Assert.Equal("unknown", ClientAddress.PartitionOf(context));
        Assert.Equal("unknown", ClientAddress.PartitionOf((IPAddress?)null));
        Assert.Equal("unknown", ClientAddress.Unknown);
    }
}
