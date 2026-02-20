using System.Net;
using System.Net.Sockets;

namespace Auth.Server.Network;

/// <summary>
/// The client address of a request (spec 0008): the connection's remote address, or, from a trusted proxy, the address the proxy
/// forwarded (see <see cref="ClientAddressSetup"/>, which has already replaced the connection's address by then). An IPv4 address
/// that reaches Kestrel mapped into IPv6 is the IPv4 address. The rate limiter partitions on it and the audit log records it.
/// </summary>
public static class ClientAddress
{
    /// <summary>The address of a request that has none (the test host).</summary>
    public const string Unknown = "unknown";

    public static IPAddress? Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var address = context.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    /// <summary>The whole address as text, or <see cref="Unknown"/>.</summary>
    public static string Text(HttpContext context) => Of(context)?.ToString() ?? Unknown;

    public static string PartitionOf(HttpContext context) => PartitionOf(Of(context));

    /// <summary>An IPv4 address as it is; an IPv6 address by its <c>/64</c> network, since one subscriber holds a whole <c>/64</c>.</summary>
    public static string PartitionOf(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out _))
        {
            return address.ToString();
        }

        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }
}
