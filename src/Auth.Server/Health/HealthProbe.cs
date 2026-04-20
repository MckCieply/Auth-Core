using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Auth.Server.Health;

/// <summary>
/// <c>Auth.Server.dll --health-probe</c>: the container's health check. The production image has no shell and no curl, so the
/// binary asks itself: one GET of <c>/auth/health</c> on the loopback address, on the port the server listens on, and the exit code
/// says what came back. It runs before the host is built, so it needs no key, no database and no mail setting, and it opens no
/// listener. It checks that the process is alive and answering, nothing more: <c>/auth/health</c> does not reach the database, and
/// <c>Degraded</c> (a manifest or a sign-up address that is not usable) is still a <c>200</c> and still a pass.
/// The address is read from the two variables the image uses (<c>ASPNETCORE_URLS</c>, <c>ASPNETCORE_HTTP_PORTS</c>), in Kestrel's
/// order; a server that listens only on one address that is not a loopback address (<c>http://10.0.0.5:8080</c>) is not found.
/// </summary>
public static class HealthProbe
{
    public const string Argument = "--health-probe";

    /// <summary>The port Kestrel uses when no setting names one.</summary>
    private const int KestrelDefaultPort = 5000;

    private const int HttpDefaultPort = 80;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The exit code: 0 when the server answered the health path with a 2xx, 1 in every other case.</summary>
    public static async Task<int> RunAsync(Func<string, string?> environment, TextWriter error, TimeSpan? timeout = null)
    {
        if (ListeningEndpoint(environment) is not { } endpoint)
        {
            await error.WriteLineAsync("health probe: the server is not told to listen on any plain-HTTP address");
            return 1;
        }

        var (address, port) = endpoint;

        try
        {
            using var handler = CreateHandler();
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = timeout ?? DefaultTimeout };
            var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            using var response = await client.GetAsync(new Uri($"http://{host}:{port}/auth/health", UriKind.Absolute));
            if (response.IsSuccessStatusCode)
            {
                return 0;
            }

            await error.WriteLineAsync($"health probe: port {port} answered {(int)response.StatusCode}");
            return 1;
        }
        catch (Exception exception)
        {
            // Whatever goes wrong is an unhealthy answer with a one-line reason, not a stack trace: only the type, never a message.
            await error.WriteLineAsync($"health probe: port {port} did not answer ({exception.GetType().Name})");
            return 1;
        }
    }

    /// <summary>
    /// No proxy: HTTP_PROXY in the container's environment must not send a loopback request anywhere else. No redirect: a redirect
    /// would leave the loopback address.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new() { UseProxy = false, AllowAutoRedirect = false };

    /// <summary>
    /// The first plain-HTTP address the server is told to listen on, read the way Kestrel reads it: when <c>ASPNETCORE_URLS</c> is
    /// set it is the only one read (an https-only list leaves no plain-HTTP address: <c>null</c>), else <c>ASPNETCORE_HTTP_PORTS</c>
    /// (the image sets it to 8080), else Kestrel's own default. The address is the loopback address of the family the URL names.
    /// </summary>
    internal static (IPAddress Address, int Port)? ListeningEndpoint(Func<string, string?> environment)
    {
        var urls = Split(environment("ASPNETCORE_URLS"));
        if (urls.Length > 0)
        {
            foreach (var url in urls)
            {
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && EndpointOf(url["http://".Length..]) is { } endpoint)
                {
                    return endpoint;
                }
            }

            return null;
        }

        foreach (var entry in Split(environment("ASPNETCORE_HTTP_PORTS")))
        {
            if (int.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
            {
                return (IPAddress.Loopback, port);
            }
        }

        return (IPAddress.Loopback, KestrelDefaultPort);
    }

    private static string[] Split(string? value) =>
        (value ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The endpoint of <c>host:port/path</c>; a host in brackets (IPv6) may hold colons itself, and no port means 80.</summary>
    private static (IPAddress Address, int Port)? EndpointOf(string hostAndPort)
    {
        var path = hostAndPort.IndexOf('/', StringComparison.Ordinal);
        var authority = path < 0 ? hostAndPort : hostAndPort[..path];
        var colon = authority.LastIndexOf(':');
        var bracketEnd = authority.LastIndexOf(']');
        var host = colon > bracketEnd ? authority[..colon] : authority;
        var address = host is "[::1]" ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        if (colon <= bracketEnd)
        {
            return (address, HttpDefaultPort);
        }

        return int.TryParse(authority[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? (address, port)
            : null;
    }
}
