using System.Diagnostics.CodeAnalysis;
using System.Net;
using IPNetwork = System.Net.IPNetwork;

namespace Auth.Server.Network;

/// <summary>
/// Who may tell Auth-Core the client address (spec 0008 → Client address and trusted proxies): the networks and the single
/// addresses of the reverse proxies in front of it. Empty by default: nothing is trusted, forwarded headers are not read at all.
/// </summary>
public sealed class ProxySettings
{
    public const string KnownNetworksKey = "Auth:Proxy:KnownNetworks";
    public const string KnownProxiesKey = "Auth:Proxy:KnownProxies";

    /// <summary>The key of the framework's own switch (the environment variable <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c>).</summary>
    private const string FrameworkSwitchKey = "ForwardedHeaders_Enabled";

    public ProxySettings(IReadOnlyList<IPNetwork> networks, IReadOnlyList<IPAddress> proxies)
    {
        ArgumentNullException.ThrowIfNull(networks);
        ArgumentNullException.ThrowIfNull(proxies);

        Networks = networks;
        Proxies = proxies;
    }

    /// <summary>Nothing is trusted.</summary>
    public static ProxySettings None { get; } = new([], []);

    /// <summary>CIDR ranges whose addresses are proxies.</summary>
    public IReadOnlyList<IPNetwork> Networks { get; }

    /// <summary>Single proxy addresses; an IPv4-mapped IPv6 address is held as the IPv4 address.</summary>
    public IReadOnlyList<IPAddress> Proxies { get; }

    public bool IsEmpty => Networks.Count == 0 && Proxies.Count == 0;

    /// <summary>
    /// Reads both lists. Each key may hold one value with entries separated by commas, or a list (<c>Key:0</c>, <c>Key:1</c>);
    /// blank entries are ignored. A bad entry stops the host, naming the key and never echoing the value.
    /// </summary>
    /// <exception cref="InvalidOperationException">An entry is not a CIDR range written as its network (bits set beyond the prefix are refused), or not an address.</exception>
    public static ProxySettings Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The framework's own switch adds the forwarded-headers middleware with both lists cleared, which believes every sender,
        // ahead of everything we configure here: refuse to start with it on.
        if (string.Equals(configuration[FrameworkSwitchKey]?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The setting ASPNETCORE_FORWARDEDHEADERS_ENABLED trusts every sender of X-Forwarded-For. Remove it and list the proxies in '{KnownNetworksKey}' and '{KnownProxiesKey}' instead.");
        }

        var networks = new List<IPNetwork>();
        foreach (var entry in EntriesOf(configuration, KnownNetworksKey))
        {
            if (!IPNetwork.TryParse(entry, out var network) || network.PrefixLength == 0 || !IsWrittenAsItsNetwork(entry, network))
            {
                throw new InvalidOperationException($"Configuration value '{KnownNetworksKey}' must be a list of CIDR ranges, each a plain address, a slash and a prefix length of at least 1.");
            }

            networks.Add(network);
        }

        var proxies = new List<IPAddress>();
        foreach (var entry in EntriesOf(configuration, KnownProxiesKey))
        {
            if (!TryParsePlainAddress(entry, out var address))
            {
                throw new InvalidOperationException($"Configuration value '{KnownProxiesKey}' must be a list of IP addresses, written as plain IPv4 (four decimal numbers) or IPv6.");
            }

            proxies.Add(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
        }

        return new ProxySettings(networks, proxies);
    }

    /// <summary>
    /// <c>IPNetwork.TryParse</c> accepts <c>10.250.0.5/24</c> and quietly masks it to <c>10.250.0.0/24</c>: a typing slip that would
    /// trust a wider network than the operator wrote. The address before the slash must be the network's own base address, and a
    /// plain one (see <see cref="TryParsePlainAddress"/>).
    /// </summary>
    private static bool IsWrittenAsItsNetwork(string entry, IPNetwork network)
    {
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && TryParsePlainAddress(entry[..slash], out var written) && written.Equals(network.BaseAddress);
    }

    /// <summary>
    /// An address as an operator writes one: IPv4 as four decimal numbers exactly as the address prints (the parser alone also reads
    /// <c>10.0.7</c> as 10.0.0.7, <c>10</c> as 0.0.0.10, <c>0x0a000007</c> as 10.0.0.7 and <c>010.0.0.7</c> as 8.0.0.7), IPv6
    /// without a scope id (the parser drops <c>%eth0</c> silently).
    /// </summary>
    private static bool TryParsePlainAddress(string text, [NotNullWhen(true)] out IPAddress? address)
    {
        if (text.Contains('%', StringComparison.Ordinal) || !IPAddress.TryParse(text, out address))
        {
            address = null;
            return false;
        }

        if (!text.Contains(':', StringComparison.Ordinal) && !string.Equals(address.ToString(), text, StringComparison.Ordinal))
        {
            address = null;
            return false;
        }

        return true;
    }

    private static IEnumerable<string> EntriesOf(IConfiguration configuration, string key)
    {
        var values = new List<string?> { configuration[key] };
        values.AddRange(configuration.GetSection(key).GetChildren().Select(child => child.Value));
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }
}
