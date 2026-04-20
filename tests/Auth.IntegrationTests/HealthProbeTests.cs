using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Auth.Server.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>
/// <c>Auth.Server.dll --health-probe</c>, the health check of the production container (it has no shell and no curl): the exit code
/// is 0 when the server answers its health path with a 2xx and 1 in every other case. The server here is a real Kestrel on a free
/// loopback port, so the probe makes a real request.
/// </summary>
public sealed class HealthProbeTests
{
    private static readonly Func<string, string?> NoEnvironment = _ => null;

    /// <summary>A Kestrel on a free loopback port that answers <c>/auth/health</c> with the given status and body.</summary>
    internal static async Task<(WebApplication App, int Port)> StartAsync(int status, string body, TimeSpan? delay = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/auth/health", async (HttpContext context) =>
        {
            if (delay is { } wait)
            {
                await Task.Delay(wait, context.RequestAborted);
            }

            if (status is 301 or 302)
            {
                context.Response.Headers.Location = "/elsewhere";
            }

            context.Response.StatusCode = status;
            await context.Response.WriteAsync(body, context.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return (app, new Uri(address).Port);
    }

    private static Func<string, string?> ListeningOn(int port) =>
        name => name == "ASPNETCORE_HTTP_PORTS" ? port.ToString(CultureInfo.InvariantCulture) : null;

    private static async Task<(int Exit, string Error)> ProbeAsync(Func<string, string?> environment, TimeSpan? timeout = null)
    {
        var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await HealthProbe.RunAsync(environment, error, timeout);
        return (exit, error.ToString());
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Theory]
    [InlineData(200, "Healthy")]
    [InlineData(200, "Degraded")]   // a manifest or a sign-up address that is not usable: still alive, never a reason to restart
    public async Task A_running_server_that_answers_200_is_exit_0(int status, string body)
    {
        var (app, port) = await StartAsync(status, body);
        await using var _ = app;

        var (exit, error) = await ProbeAsync(ListeningOn(port));

        Assert.Equal(0, exit);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(302)]   // not followed: a redirect would leave the loopback address
    public async Task A_server_that_answers_anything_but_2xx_is_exit_1(int status)
    {
        var (app, port) = await StartAsync(status, "no");
        await using var _ = app;

        var (exit, error) = await ProbeAsync(ListeningOn(port));

        Assert.Equal(1, exit);
        Assert.Contains($"answered {status}", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_listening_is_exit_1()
    {
        var (exit, error) = await ProbeAsync(ListeningOn(FreePort()));

        Assert.Equal(1, exit);
        Assert.Contains("did not answer", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_in_time_is_exit_1()
    {
        var (app, port) = await StartAsync(200, "Healthy", delay: TimeSpan.FromSeconds(30));
        await using var _ = app;

        var started = DateTime.UtcNow;
        var (exit, _) = await ProbeAsync(ListeningOn(port), TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, exit);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void The_probe_uses_no_proxy_and_follows_no_redirect()   // HTTP_PROXY of the container must not take a loopback request elsewhere
    {
        using var handler = HealthProbe.CreateHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task A_list_of_https_addresses_only_leaves_nothing_to_ask_and_is_exit_1()
    {
        string? Environment(string name) => name == "ASPNETCORE_URLS" ? "https://+:8443" : null;

        var (exit, error) = await ProbeAsync(Environment);

        Assert.Equal(1, exit);
        Assert.Contains("plain-HTTP", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, "127.0.0.1:5000")]                                    // Kestrel's own default
    [InlineData(null, "8080", "127.0.0.1:8080")]
    [InlineData(null, "9090;9091", "127.0.0.1:9090")]                             // the first one
    [InlineData(null, "abc;7000", "127.0.0.1:7000")]                              // what is not a port is skipped
    [InlineData("http://+:8081", "8080", "127.0.0.1:8081")]                       // ASPNETCORE_URLS wins, as it does in Kestrel
    [InlineData("https://+:8443;http://localhost:8082/", null, "127.0.0.1:8082")] // only plain HTTP, and not the path
    [InlineData("http://[::1]:8083", null, "[::1]:8083")]
    [InlineData("https://+:8443", "8080", null)]                                  // URLS is the only one read, and it names no plain-HTTP address
    [InlineData("http://localhost", null, "127.0.0.1:80")]                        // no port written: 80
    [InlineData("http://[::1]", null, "[::1]:80")]
    public void The_address_comes_from_the_variables_the_server_reads(string? urls, string? httpPorts, string? expected)
    {
        string? Environment(string name) => name switch
        {
            "ASPNETCORE_URLS" => urls,
            "ASPNETCORE_HTTP_PORTS" => httpPorts,
            _ => null,
        };

        var endpoint = HealthProbe.ListeningEndpoint(Environment);

        Assert.Equal(expected, endpoint is { } e ? $"{(e.Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{e.Address}]" : e.Address.ToString())}:{e.Port}" : null);
    }

    [Fact]
    public void Without_variables_the_port_is_the_default() =>
        Assert.Equal((IPAddress.Loopback, 5000), HealthProbe.ListeningEndpoint(NoEnvironment));
}

/// <summary>
/// The wiring: the entry point of the binary takes the argument before it builds any host, so it needs no key and no database. It
/// changes the environment of the whole process, so it runs alone (no other test class runs at the same time).
/// </summary>
[CollectionDefinition(nameof(HealthProbeEntryPointTests), DisableParallelization = true)]
public sealed class HealthProbeEntryPointGroup;

[Collection(nameof(HealthProbeEntryPointTests))]
public sealed class HealthProbeEntryPointTests
{
    private static readonly string[] ProbeArguments = ["--health-probe"];

    [Fact]
    public async Task The_entry_point_runs_the_probe_without_building_the_host()
    {
        var (app, port) = await HealthProbeTests.StartAsync(200, "Healthy");
        await using var _ = app;
        var entry = typeof(Program).Assembly.EntryPoint!;
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", port.ToString(CultureInfo.InvariantCulture));
            var up = (int)entry.Invoke(null, [ProbeArguments])!;

            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", HealthProbeTests.FreePort().ToString(CultureInfo.InvariantCulture));
            var down = (int)entry.Invoke(null, [ProbeArguments])!;

            Assert.Equal(0, up);
            Assert.Equal(1, down);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", urls);
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", ports);
        }
    }
}
