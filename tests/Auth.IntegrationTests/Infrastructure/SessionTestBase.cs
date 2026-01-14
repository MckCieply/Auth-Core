using Microsoft.Extensions.Time.Testing;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Base of the session tests (refresh, logout): a host with a controllable clock and a client that carries the
/// refresh cookie by hand (see <see cref="SessionApi"/>).
/// </summary>
public abstract class SessionTestBase : IAsyncLifetime
{
    protected SessionTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
    {
        Postgres = postgres;
        Keys = keys;
        // Starts at the real time and only ever moves forward, so tokens stay valid for system-clock validators.
        Clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Factory = new AuthAppFactory(postgres, keys).WithClock(Clock);
    }

    protected PostgresFixture Postgres { get; }

    protected KeyMaterialFixture Keys { get; }

    protected FakeTimeProvider Clock { get; }

    protected AuthAppFactory Factory { get; }

    protected HttpClient Client { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        Client = SessionApi.CreateClient(Factory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
