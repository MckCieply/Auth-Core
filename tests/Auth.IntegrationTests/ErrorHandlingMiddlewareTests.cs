using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>
/// The handler driven directly, on a bare <see cref="DefaultHttpContext"/>: the cases the in-memory test server cannot make
/// (Kestrel's own rejections of a request body, a client that has gone, a response that has started). One theory runs the
/// handler and the security headers in a minimal test-server pipeline, in the order Program.cs gives them, with a final step
/// that throws as a body read does.
/// </summary>
public sealed class ErrorHandlingMiddlewareTests
{
    /// <summary>A response that has started, as one does once the headers have gone out.</summary>
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    /// <summary>What the server's connection offers: <c>Abort</c> trips <c>RequestAborted</c>, as the real one does.</summary>
    private sealed class AbortableConnection : IHttpRequestLifetimeFeature, IDisposable
    {
        private readonly CancellationTokenSource _aborted = new();

        public CancellationToken RequestAborted
        {
            get => _aborted.Token;
            set => throw new NotSupportedException();
        }

        public void Abort() => _aborted.Cancel();

        public void Dispose() => _aborted.Dispose();
    }

    private sealed record Run(DefaultHttpContext Http, string Body, CapturingLoggerProvider Logs, Exception? Thrown);

    private static async Task<Run> RunAsync(RequestDelegate next, Action<DefaultHttpContext>? arrange = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/auth/login";
        http.Response.Body = new MemoryStream();
        http.RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();   // what writing a JSON result needs
        arrange?.Invoke(http);

        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var middleware = new ErrorHandlingMiddleware(next, factory.CreateLogger<ErrorHandlingMiddleware>());

        Exception? thrown = null;
        try
        {
            await middleware.InvokeAsync(http);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        http.Response.Body.Position = 0;
        return new Run(http, await new StreamReader(http.Response.Body).ReadToEndAsync(), logs, thrown);
    }

    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    [InlineData(StatusCodes.Status400BadRequest)]
    public async Task A_request_the_server_rejected_is_answered_with_its_own_status_no_store_and_invalid_request(int status)
    {
        var run = await RunAsync(_ => throw new BadHttpRequestException("rejected-by-the-server", status));

        Assert.Null(run.Thrown);
        Assert.Equal(status, run.Http.Response.StatusCode);
        Assert.Equal("""{"error":"invalid_request"}""", run.Body);
        Assert.StartsWith("application/json", run.Http.Response.ContentType, StringComparison.Ordinal);
        Assert.Equal("no-store", run.Http.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-cache", run.Http.Response.Headers.Pragma.ToString());
        Assert.Contains(run.Logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("rejected-by-the-server", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Logs.Entries, e => e.Level >= LogLevel.Warning);   // the client's fault, not an error of ours
    }

    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    [InlineData(StatusCodes.Status400BadRequest)]
    public async Task A_request_the_server_rejected_has_the_security_headers_in_the_pipeline_of_the_service(int status)   // criterion 4
    {
        // The two middleware in the order Program.cs gives them, with a terminal that fails the way a body read does.
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseErrorHandling();
                    app.UseSecurityHeaders();
                    app.Run(_ => throw new BadHttpRequestException("rejected-by-the-server", status));
                }))
            .StartAsync(TestContext.Current.CancellationToken);

        using var response = await host.GetTestClient().PostAsync("/auth/login", new StringContent("{}"), TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("""{"error":"invalid_request"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        SecurityHeadersApi.AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task A_request_the_server_rejected_loses_what_the_request_had_set_the_cookie_included()
    {
        var run = await RunAsync(http =>
        {
            http.Response.Headers.SetCookie = "auth_rt=x";
            throw new BadHttpRequestException("rejected", StatusCodes.Status413PayloadTooLarge);
        });

        Assert.Equal("", run.Http.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task A_client_that_has_gone_is_not_answered_and_nothing_is_logged()
    {
        var run = await RunAsync(
            _ => throw new OperationCanceledException(),
            http => http.RequestAborted = new CancellationToken(canceled: true));

        Assert.Null(run.Thrown);
        Assert.Equal(StatusCodes.Status200OK, run.Http.Response.StatusCode);
        Assert.Equal("", run.Body);
        Assert.Empty(run.Logs.Entries);
    }

    [Fact]
    public async Task An_exception_after_the_response_has_started_ends_the_connection_and_is_logged_once()
    {
        using var connection = new AbortableConnection();
        var run = await RunAsync(
            _ => throw new InvalidOperationException("late-boom"),
            http =>
            {
                http.Features.Set<IHttpResponseFeature>(new StartedResponseFeature { Body = http.Response.Body });
                http.Features.Set<IHttpRequestLifetimeFeature>(connection);
            });

        Assert.Null(run.Thrown);   // not rethrown: the server would log it a second time
        Assert.Equal("", run.Body);   // nothing is added to a response that has gone out
        Assert.True(run.Http.RequestAborted.IsCancellationRequested, "The connection is aborted.");
        var logged = Assert.Single(run.Logs.Entries);
        Assert.Equal(LogLevel.Error, logged.Level);
        Assert.Contains("late-boom", logged.Message, StringComparison.Ordinal);
    }
}
