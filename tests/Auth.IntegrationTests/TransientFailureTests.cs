using System.Net.Sockets;
using Auth.Server.Api;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Auth.IntegrationTests;

public sealed class TransientFailureTests
{
    [Fact]
    public void A_transient_database_failure_a_timeout_and_a_socket_error_are_found_at_any_depth()
    {
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(new NpgsqlException("down", new SocketException())));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(new NpgsqlException("down", new IOException("reset"))));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(
            new InvalidOperationException("outer", new NpgsqlException("down", new IOException("reset", new SocketException())))));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(new DbUpdateException("save", new NpgsqlException("down", new SocketException()))));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(new TimeoutException()));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(new SocketException()));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(
            new AggregateException(new ArgumentException("a"), new InvalidOperationException("b", new TimeoutException()))));
    }

    [Theory]
    [InlineData(PostgresErrorCodes.CannotConnectNow)]   // 57P03: the server is starting up, as right after it was started again
    [InlineData(PostgresErrorCodes.AdminShutdown)]   // 57P01: the server is shutting down, as when it is stopped under a connection
    public void A_postgres_server_that_is_starting_up_or_shutting_down_is_the_outage_of_a_dependency(string sqlState)
    {
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(new PostgresException("starting up", "FATAL", "FATAL", sqlState)));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(
            new InvalidOperationException("outer", new PostgresException("starting up", "FATAL", "FATAL", sqlState))));
    }

    [Fact]
    public void Anything_else_is_not_the_outage_of_a_dependency()
    {
        Assert.False(ErrorHandlingMiddleware.IsTransientFailure(new InvalidOperationException("x")));
        Assert.False(ErrorHandlingMiddleware.IsTransientFailure(new NpgsqlException("a failure that is not transient")));
        Assert.False(ErrorHandlingMiddleware.IsTransientFailure(new ArgumentNullException("parameter")));
        Assert.False(ErrorHandlingMiddleware.IsTransientFailure(new DbUpdateException("save", new InvalidOperationException("x"))));
        Assert.False(ErrorHandlingMiddleware.IsTransientFailure(new AggregateException(new ArgumentException("a"))));
    }
}
