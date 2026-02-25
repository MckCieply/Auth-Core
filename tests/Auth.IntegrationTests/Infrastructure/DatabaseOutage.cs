using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// A TCP forwarder between a host and the test PostgreSQL, to stop and start "the database" for one host without touching the
/// server the other tests share. <see cref="Cut"/> closes every connection through it and refuses new ones (what a stopped
/// server does); <see cref="Start"/> lets connections through again, on the same port. A host that is made to use
/// <see cref="ConnectionString"/> sees an outage and a recovery.
/// </summary>
public sealed class DatabaseOutage : IAsyncDisposable
{
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly object _gate = new();
    private readonly List<TcpClient> _connections = [];
    private TcpListener? _listener;

    public DatabaseOutage(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var upstream = new NpgsqlConnectionStringBuilder(connectionString);
        _upstreamHost = upstream.Host ?? "localhost";
        _upstreamPort = upstream.Port;

        // Take a free port once and keep it for every Start. The listener that finds it is the first one that serves: it is not
        // stopped and bound again, which would leave a window in which something else could take the port.
        var first = new TcpListener(IPAddress.Loopback, 0);
        first.Start();
        Port = ((IPEndPoint)first.LocalEndpoint).Port;
        _listener = first;
        _ = Task.Run(() => AcceptAsync(first));

        upstream.Host = "127.0.0.1";
        upstream.Port = Port;
        ConnectionString = upstream.ConnectionString;
    }

    public int Port { get; }

    /// <summary>The connection string of the upstream database, with the host and the port of the forwarder.</summary>
    public string ConnectionString { get; }

    public void Start()
    {
        lock (_gate)
        {
            if (_listener is not null)
            {
                return;
            }

            var listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            _listener = listener;
            _ = Task.Run(() => AcceptAsync(listener));
        }

        ClearPool();
    }

    public void Cut()
    {
        lock (_gate)
        {
            _listener?.Stop();
            _listener = null;
            foreach (var connection in _connections)
            {
                connection.Close();
            }

            _connections.Clear();
        }

        ClearPool();
    }

    /// <summary>
    /// The host's connection pool holds connections that were opened before the cut: they are dead sockets, and would be handed to the
    /// first requests after <see cref="Start"/> (or to the first after <see cref="Cut"/>, which then fail slowly instead of at once).
    /// Both ends empty the pool, so that every connection after either is a new one.
    /// </summary>
    private void ClearPool()
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    public ValueTask DisposeAsync()
    {
        Cut();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return; // stopped
            }

            var upstream = new TcpClient { NoDelay = true };
            try
            {
                await upstream.ConnectAsync(_upstreamHost, _upstreamPort);
            }
            catch (SocketException)
            {
                client.Close();
                upstream.Close();
                continue;
            }

            client.NoDelay = true;
            lock (_gate)
            {
                // A Cut while this connection was being made: it must not carry traffic after it.
                if (!ReferenceEquals(_listener, listener))
                {
                    client.Close();
                    upstream.Close();
                    return;
                }

                _connections.Add(client);
                _connections.Add(upstream);
            }

            _ = PumpAsync(client, upstream);
            _ = PumpAsync(upstream, client);
        }
    }

    private static async Task PumpAsync(TcpClient from, TcpClient to)
    {
        try
        {
            await from.GetStream().CopyToAsync(to.GetStream());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or SocketException)
        {
            // one side went away
        }
        finally
        {
            to.Close();
            from.Close();
        }
    }
}
