using Auth.FakeOidc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// The fake provider in process, behind a test server. OpenIddict's HTTP clients reach it through <see cref="Routing"/> whatever host they ask
/// for (accounts.google.com, login.microsoftonline.com, a test issuer), so nothing leaves the process; a test plays the browser at the fake with
/// <see cref="Client"/>, which never follows a redirect. It shares the test's clock, so the identity tokens it signs are valid at the time the host
/// sees.
/// </summary>
public sealed class FakeOidcHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpMessageHandler _handler;

    public FakeOidcHost(TimeProvider clock, FakeOidcOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        Server = new FakeOidcServer(options ?? new FakeOidcOptions(), clock);
        _app.Run(Server.HandleAsync);
        // A test server starts at once; the fake must be up before the first host of the test asks it for anything.
        _app.StartAsync().GetAwaiter().GetResult();
        _handler = _app.GetTestServer().CreateHandler();
        Client = new HttpClient(_handler, disposeHandler: false);
        Routing = new FakeOidcRouting(_handler);
    }

    public FakeOidcServer Server { get; }

    public HttpClient Client { get; }

    /// <summary>Register it in a host (<c>services.AddSingleton(Routing)</c>) before the host is built.</summary>
    public IPostConfigureOptions<HttpClientFactoryOptions> Routing { get; }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        _handler.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Server.Dispose();
    }
}

/// <summary>
/// Sends every request of OpenIddict's named HTTP clients, whose names start with the name of their assembly, to the fake (probe Q6). The
/// primary handler must stay an <c>HttpClientHandler</c>, so the fake is put in as an additional handler that never calls the inner one.
/// </summary>
internal sealed class FakeOidcRouting(HttpMessageHandler fake) : IPostConfigureOptions<HttpClientFactoryOptions>
{
    private const string OpenIddictClients = "OpenIddict.Client.SystemNetHttp";

    public void PostConfigure(string? name, HttpClientFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (name is null || !name.StartsWith(OpenIddictClients, StringComparison.Ordinal))
        {
            return;
        }

        options.HttpMessageHandlerBuilderActions.Add(builder => builder.AdditionalHandlers.Add(new ToTheFake(fake)));
    }

    private sealed class ToTheFake(HttpMessageHandler fake) : DelegatingHandler
    {
        private readonly HttpMessageInvoker _invoker = new(fake, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _invoker.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _invoker.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
