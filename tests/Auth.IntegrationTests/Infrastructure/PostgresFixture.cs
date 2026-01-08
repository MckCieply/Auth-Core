using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Auth.IntegrationTests.Infrastructure.PostgresFixture))]

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// One <c>postgres:16-alpine</c> container for the whole test assembly (xUnit v3 assembly fixture).
/// Tests isolate themselves by using a database of their own on this server.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>
    /// Connection string for <paramref name="database"/> on the shared server.
    /// The database itself is created by EF's <c>Migrate</c> on first use.
    /// </summary>
    public string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;
}
