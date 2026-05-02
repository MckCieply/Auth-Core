using System.Net.Sockets;
using Auth.IntegrationTests.Infrastructure;
using Npgsql;

namespace Auth.IntegrationTests;

/// <summary>Which failures the test host's database set-up repeats: a connection attempt that hung, and nothing else.</summary>
public class AuthAppFactoryRetryTests
{
    [Fact]
    public void A_timeout_is_a_connection_timeout()
    {
        Assert.True(AuthAppFactory.IsConnectionTimeout(new TimeoutException("Timeout during connection attempt")));
    }

    [Fact]
    public void An_npgsql_exception_holding_a_timeout_is_a_connection_timeout()
    {
        Assert.True(AuthAppFactory.IsConnectionTimeout(new NpgsqlException("Exception while connecting", new TimeoutException())));
    }

    [Fact]
    public void An_npgsql_exception_that_says_the_connection_attempt_failed_is_a_connection_timeout()
    {
        Assert.True(AuthAppFactory.IsConnectionTimeout(new NpgsqlException("Failed to connect to 192.168.65.254:49153")));
    }

    [Fact]
    public void Other_failures_are_not_connection_timeouts()
    {
        Assert.False(AuthAppFactory.IsConnectionTimeout(new NpgsqlException("password authentication failed for user \"postgres\"")));
        Assert.False(AuthAppFactory.IsConnectionTimeout(new NpgsqlException("relation \"x\" already exists", new IOException("closed"))));
        Assert.False(AuthAppFactory.IsConnectionTimeout(new InvalidOperationException("Failed to connect")));
        Assert.False(AuthAppFactory.IsConnectionTimeout(new SocketException()));
    }
}
