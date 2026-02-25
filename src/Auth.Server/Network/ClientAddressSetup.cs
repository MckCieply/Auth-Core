using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Auth.Server.Network;

public static class ClientAddressSetup
{
    /// <summary>
    /// The forwarded-headers options of spec 0008: <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> only (never the host), from
    /// the configured proxies only, every hop walked. The framework's own defaults (loopback trusted, one hop) are removed. Both
    /// lists must not be left empty: the middleware then trusts every sender, which is why <see cref="UseClientAddress"/> adds
    /// nothing when the settings are empty.
    /// </summary>
    public static ForwardedHeadersOptions OptionsFor(ProxySettings proxies)
    {
        ArgumentNullException.ThrowIfNull(proxies);

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = null,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var network in proxies.Networks)
        {
            options.KnownIPNetworks.Add(network);
        }

        foreach (var proxy in proxies.Proxies)
        {
            options.KnownProxies.Add(proxy);
        }

        return options;
    }

    /// <summary>Makes <see cref="ClientAddress"/> follow a trusted proxy. With no proxy configured a forwarded header is not read at all.</summary>
    public static IApplicationBuilder UseClientAddress(this IApplicationBuilder app, ProxySettings proxies)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(proxies);

        return proxies.IsEmpty ? app : app.UseForwardedHeaders(OptionsFor(proxies));
    }
}
