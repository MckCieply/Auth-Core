using System.Net;
using Auth.Server.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class ClientAddressSetupTests
{
    private static readonly ProxySettings Network = new([System.Net.IPNetwork.Parse("10.250.0.0/24")], []);

    private sealed record Seen(string? Remote, string Scheme, string Host);

    private static async Task<Seen> RunAsync(
        ProxySettings proxies, string remote, string? forwardedFor = null, string? proto = null, string? forwardedHost = null)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseClientAddress(proxies);
        Seen? seen = null;
        app.Run(context =>
        {
            seen = new Seen(context.Connection.RemoteIpAddress?.ToString(), context.Request.Scheme, context.Request.Host.Value ?? "");
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("auth.example");
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        if (proto is not null)
        {
            context.Request.Headers["X-Forwarded-Proto"] = proto;
        }

        if (forwardedHost is not null)
        {
            context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        }

        await app.Build()(context);
        return seen!;
    }

    [Fact]
    public async Task With_no_proxy_configured_a_forwarded_for_header_is_ignored()   // criterion 2
    {
        var seen = await RunAsync(ProxySettings.None, "203.0.113.9", forwardedFor: "1.1.1.1", proto: "https");

        Assert.Equal("203.0.113.9", seen.Remote);
        Assert.Equal("http", seen.Scheme);
    }

    [Fact]
    public async Task With_no_proxy_configured_the_framework_default_of_trusting_loopback_is_not_used()
    {
        var seen = await RunAsync(ProxySettings.None, "127.0.0.1", forwardedFor: "1.1.1.1");

        Assert.Equal("127.0.0.1", seen.Remote);
    }

    [Fact]
    public async Task A_configured_network_does_not_trust_loopback_as_the_framework_would()
    {
        var seen = await RunAsync(Network, "127.0.0.1", forwardedFor: "1.1.1.1");

        Assert.Equal("127.0.0.1", seen.Remote);
    }

    [Fact]
    public async Task From_a_trusted_proxy_the_client_is_the_last_address_that_is_not_a_proxy()   // criterion 2
    {
        var seen = await RunAsync(Network, "10.250.0.2", forwardedFor: "198.51.100.7");

        Assert.Equal("198.51.100.7", seen.Remote);
    }

    [Fact]
    public async Task An_address_the_client_wrote_into_the_header_is_not_the_client()   // the proxy appends the real one
    {
        var seen = await RunAsync(Network, "10.250.0.2", forwardedFor: "9.9.9.9, 198.51.100.7");

        Assert.Equal("198.51.100.7", seen.Remote);
    }

    [Fact]
    public async Task Every_hop_is_walked_not_just_the_first()
    {
        var seen = await RunAsync(Network, "10.250.0.2", forwardedFor: "9.9.9.9, 198.51.100.7, 10.250.0.9");

        Assert.Equal("198.51.100.7", seen.Remote);
    }

    [Fact]
    public async Task From_an_untrusted_address_the_headers_are_ignored()
    {
        var seen = await RunAsync(Network, "203.0.113.9", forwardedFor: "198.51.100.7", proto: "https");

        Assert.Equal("203.0.113.9", seen.Remote);
        Assert.Equal("http", seen.Scheme);
    }

    [Fact]
    public async Task The_scheme_is_taken_from_a_trusted_proxy_and_the_host_never_is()
    {
        var seen = await RunAsync(Network, "10.250.0.2", forwardedFor: "198.51.100.7", proto: "https", forwardedHost: "evil.example");

        Assert.Equal("https", seen.Scheme);
        Assert.Equal("auth.example", seen.Host);
    }

    [Fact]
    public async Task A_listed_proxy_is_trusted_also_when_kestrel_reports_it_mapped_into_ipv6()
    {
        var proxies = new ProxySettings([], [IPAddress.Parse("10.0.0.7")]);

        var seen = await RunAsync(proxies, "::ffff:10.0.0.7", forwardedFor: "198.51.100.7");

        Assert.Equal("198.51.100.7", seen.Remote);
    }

    [Fact]
    public void The_options_name_exactly_the_configured_proxies_and_walk_every_hop()
    {
        var options = ClientAddressSetup.OptionsFor(new ProxySettings([System.Net.IPNetwork.Parse("10.250.0.0/24")], [IPAddress.Parse("10.0.0.7")]));

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Null(options.ForwardLimit);
        Assert.Equal([IPAddress.Parse("10.0.0.7")], options.KnownProxies);
        Assert.Equal(["10.250.0.0/24"], options.KnownIPNetworks.Select(n => n.ToString()));
    }
}
