using System.Net;
using System.Text;
using Auth.Server.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using OpenIddict.Abstractions;
using OpenIddict.Client;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012 → GitHub, steps 2 to 4 (Amendments A3, A4): the reader's own rules, driven directly with a stub client and the fake clock. The round
/// trips through the fake provider are the callback tests'; what no answer of the fake can show is here: the bounds, the boundary of 64 KiB, the
/// revocation that outlives the request, the secrets that must not leak, and what the reader leaves alone.
/// </summary>
public sealed class GitHubReaderTests
{
    private const string Token = "gho_the-access-token-of-ola";
    private const string ClientId = "gh-client-id";
    private const string ClientSecret = "gh-client-secret-value";

    private const string UserJson = """{"id":5,"login":"ola-gh"}""";
    private const string EmailsJson = """[{"email":"a@github.test","primary":true,"verified":true}]""";

    private static readonly TimeSpan Real = TimeSpan.FromSeconds(10);

    private static HttpResponseMessage Json(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    /// <summary>Valid JSON of exactly <paramref name="bytes"/> bytes: the document, with its last member padded.</summary>
    private static string Padded(string shape, int bytes)
    {
        var empty = shape.Replace("~", string.Empty, StringComparison.Ordinal);
        var padding = bytes - Encoding.UTF8.GetByteCount(empty);
        Assert.True(padding >= 0);
        return shape.Replace("~", new string('a', padding), StringComparison.Ordinal);
    }

    private static string PaddedUser(int bytes) => Padded("""{"id":5,"pad":"~"}""", bytes);

    private static string PaddedEmails(int bytes) => Padded("""[{"email":"a@github.test","primary":true,"verified":true,"pad":"~"}]""", bytes);

    private sealed record Call(HttpMethod Method, Uri Uri, string? Authorization, string Body, bool CancelledOnArrival);

    private sealed class Capture : ILogger<GitHubReader>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Harness
    {
        public FakeTimeProvider Clock { get; } = new();

        public List<Call> Calls { get; } = [];

        public Capture Logs { get; } = new();

        /// <summary>Released by every handler that waits, so that a test advances the clock only when the reader is really waiting.</summary>
        public SemaphoreSlim Waiting { get; } = new(0);

        public Func<CancellationToken, Task<HttpResponseMessage>> User { get; set; } = _ => Task.FromResult(Json(UserJson));

        public Func<CancellationToken, Task<HttpResponseMessage>> Emails { get; set; } = _ => Task.FromResult(Json(EmailsJson));

        public Func<CancellationToken, Task<HttpResponseMessage>> Revoke { get; set; } = _ => Task.FromResult(Status(HttpStatusCode.NoContent));

        public GitHubReader Reader(bool testIssuer) => new(new Factory(this), Settings(testIssuer), Clock, Logs);

        public async Task WaitUntilWaitingAsync() => Assert.True(await Waiting.WaitAsync(Real));

        /// <summary>Waits without end for the clock or the request to end the wait.</summary>
        public async Task<HttpResponseMessage> StallAsync(CancellationToken cancellationToken)
        {
            Waiting.Release();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public async Task<HttpResponseMessage> AfterAsync(TimeSpan time, string body, CancellationToken cancellationToken)
        {
            var delay = Task.Delay(time, Clock, cancellationToken);
            Waiting.Release();
            await delay;
            return Json(body);
        }

        /// <summary>Headers at once; the body breaks after some bytes, as a connection that is reset does (<paramref name="beforeBreak"/> runs first).</summary>
        public static HttpResponseMessage BrokenBody(Action? beforeBreak = null) =>
            new(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(beforeBreak)) };

        /// <summary>Headers at once; the body never comes.</summary>
        public HttpResponseMessage StalledBody() =>
            new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(Waiting)) };

        private sealed class Factory(Harness harness) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                Assert.Equal(GitHubReader.HttpClientName, name);
                return new HttpClient(new Stub(harness), disposeHandler: true);
            }
        }

        private sealed class Stub(Harness harness) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(CancellationToken.None);
                harness.Calls.Add(new Call(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body, cancellationToken.IsCancellationRequested));
                if (request.Method == HttpMethod.Delete)
                {
                    return await harness.Revoke(cancellationToken);
                }

                return request.RequestUri!.AbsolutePath.EndsWith("/emails", StringComparison.Ordinal)
                    ? await harness.Emails(cancellationToken)
                    : await harness.User(cancellationToken);
            }
        }
    }

    private sealed class BrokenStream(Action? beforeBreak) : Stream
    {
        private bool _sentSome;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentSome)
            {
                _sentSome = true;
                buffer.Span[0] = (byte)'[';
                return ValueTask.FromResult(1);
            }

            beforeBreak?.Invoke();
            throw new IOException("The connection was reset while the body was read.");
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledStream(SemaphoreSlim waiting) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            waiting.Release();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static ProviderSettings Settings(bool testIssuer) => ProviderSettings.Load(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ProviderSettings.ReturnUrlKey] = "http://localhost:4200/cb",
            [ProviderSettings.GitHubClientIdKey] = ClientId,
            [ProviderSettings.GitHubClientSecretKey] = ClientSecret,
            [ProviderSettings.TestIssuerKey] = testIssuer ? "http://fake.test/" : null,
        }).Build(),
        isDevelopment: true);

    private static OpenIddictClientEvents.ProcessAuthenticationContext Context(
        string? token = Token, CancellationToken aborted = default)
    {
        var registration = new OpenIddictClientRegistration
        {
            ProviderName = ProviderNames.GitHub,
            ClientId = ClientId,
            ClientSecret = ClientSecret,
            Issuer = new Uri("http://issuer.test/"),
        };
        return new OpenIddictClientEvents.ProcessAuthenticationContext(new OpenIddictClientTransaction())
        {
            EndpointType = OpenIddictClientEndpointType.Redirection,
            Registration = registration,
            TokenResponse = new OpenIddictResponse(),
            BackchannelAccessToken = token,
            RequireBackchannelAccessToken = true,
            CancellationToken = aborted,
        };
    }

    private static string Basic() => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":" + ClientSecret));

    private static void AssertHandedOver(OpenIddictClientEvents.ProcessAuthenticationContext context)
    {
        Assert.False(context.IsRejected);
        Assert.Equal(new GitHubAccount("5", "a@github.test", true), GitHubItems.Read(context.Properties));
        AssertTokenGone(context);
    }

    private static void AssertTokenGone(OpenIddictClientEvents.ProcessAuthenticationContext context)
    {
        Assert.Null(context.BackchannelAccessToken);
        Assert.False(context.RequireBackchannelAccessToken);
        Assert.DoesNotContain(context.Properties.Values, value => value is not null && value.Contains(Token, StringComparison.Ordinal));
    }

    private static void AssertRefused(OpenIddictClientEvents.ProcessAuthenticationContext context)
    {
        Assert.True(context.IsRejected);
        Assert.Equal(OpenIddictConstants.Errors.ServerError, context.Error);
        Assert.Equal("urn:auth-core:github-read", context.ErrorUri);
        Assert.False(string.IsNullOrEmpty(context.ErrorDescription));
        Assert.DoesNotContain(Token, context.ErrorDescription, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientSecret, context.ErrorDescription, StringComparison.Ordinal);
        Assert.Null(GitHubItems.Read(context.Properties));
    }

    private static void AssertRevokedOnce(Harness harness)
    {
        var revocation = Assert.Single(harness.Calls, call => call.Method == HttpMethod.Delete);
        Assert.EndsWith($"/applications/{ClientId}/token", revocation.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(Basic(), revocation.Authorization);
        Assert.Equal($$"""{"access_token":"{{Token}}"}""", revocation.Body);
        Assert.False(revocation.CancelledOnArrival);
    }

    private static void AssertNoSecretLogged(Harness harness)
    {
        Assert.DoesNotContain(harness.Logs.Entries, entry => entry.Message.Contains(Token, StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Logs.Entries, entry => entry.Message.Contains(ClientSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Logs.Entries, entry => entry.Message.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":" + ClientSecret)), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "https://api.github.com")]
    [InlineData(true, "http://fake.test/github")]
    public async Task The_reads_and_the_revocation_go_to_the_api_of_the_mode_with_the_registrations_own_credentials(bool testIssuer, string api)   // finding 2
    {
        var harness = new Harness();
        var context = Context();

        await harness.Reader(testIssuer).HandleAsync(context);

        AssertHandedOver(context);
        Assert.Equal(
            [
                (HttpMethod.Get, api + "/user", "Bearer " + Token),
                (HttpMethod.Get, api + "/user/emails?per_page=100", "Bearer " + Token),
                (HttpMethod.Delete, api + $"/applications/{ClientId}/token", Basic()),
            ],
            harness.Calls.Select(call => (call.Method, call.Uri.ToString(), call.Authorization)));
        AssertRevokedOnce(harness);
        Assert.Empty(harness.Logs.Entries);
    }

    [Fact]
    public async Task The_revocation_never_uses_the_token_of_the_request_that_was_cancelled_before_the_read()   // finding 1
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var harness = new Harness();
        var context = Context(aborted: aborted.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Reader(false).HandleAsync(context).AsTask());

        Assert.Equal([HttpMethod.Get, HttpMethod.Delete], harness.Calls.Select(call => call.Method));
        Assert.True(harness.Calls[0].CancelledOnArrival);
        AssertRevokedOnce(harness);
        AssertTokenGone(context);
    }

    [Fact]
    public async Task The_revocation_never_uses_the_token_of_the_request_that_was_cancelled_during_the_read()   // finding 1
    {
        using var aborted = new CancellationTokenSource();
        var harness = new Harness();
        harness.User = async cancellationToken =>
        {
            await aborted.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        };
        var context = Context(aborted: aborted.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Reader(false).HandleAsync(context).AsTask());

        Assert.Equal([HttpMethod.Get, HttpMethod.Delete], harness.Calls.Select(call => call.Method));
        AssertRevokedOnce(harness);
        AssertTokenGone(context);
    }

    [Theory]
    [InlineData("user", 65_536, true)]
    [InlineData("user", 65_537, false)]
    [InlineData("emails", 65_536, true)]
    [InlineData("emails", 65_537, false)]
    public async Task A_document_of_exactly_64_KiB_is_read_and_one_byte_more_is_refused(string which, int bytes, bool accepted)   // finding 3
    {
        var harness = new Harness();
        if (which == "user")
        {
            harness.User = _ => Task.FromResult(Json(PaddedUser(bytes)));
        }
        else
        {
            harness.Emails = _ => Task.FromResult(Json(PaddedEmails(bytes)));
        }

        var context = Context();
        await harness.Reader(false).HandleAsync(context);

        if (accepted)
        {
            AssertHandedOver(context);
        }
        else
        {
            AssertRefused(context);
            AssertTokenGone(context);
        }

        AssertRevokedOnce(harness);
    }

    [Fact]
    public async Task A_body_that_never_comes_after_the_headers_is_cut_at_the_bound_of_the_call_of_user()   // finding 4
    {
        var harness = new Harness();
        harness.User = _ => Task.FromResult(harness.StalledBody());
        var context = Context();

        var read = harness.Reader(false).HandleAsync(context).AsTask();
        await harness.WaitUntilWaitingAsync();
        harness.Clock.Advance(GitHubReader.CallBound + TimeSpan.FromSeconds(1));
        await read.WaitAsync(Real);

        AssertRefused(context);
        AssertTokenGone(context);
        AssertRevokedOnce(harness);
        Assert.DoesNotContain(harness.Calls, call => call.Uri.AbsolutePath.EndsWith("/emails", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_body_that_never_comes_after_the_headers_is_cut_at_the_bound_of_the_call_of_emails()   // finding 4
    {
        var harness = new Harness();
        harness.Emails = _ => Task.FromResult(harness.StalledBody());
        var context = Context();

        var read = harness.Reader(false).HandleAsync(context).AsTask();
        await harness.WaitUntilWaitingAsync();
        harness.Clock.Advance(GitHubReader.CallBound + TimeSpan.FromSeconds(1));
        await read.WaitAsync(Real);

        AssertRefused(context);
        AssertTokenGone(context);
        AssertRevokedOnce(harness);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("emails")]
    public async Task A_body_cut_by_the_connection_while_it_is_read_is_refused_and_the_token_is_revoked_once(string document)   // an IOException is a failed read, not a crash
    {
        var harness = new Harness();
        if (document == "user")
        {
            harness.User = _ => Task.FromResult(Harness.BrokenBody());
        }
        else
        {
            harness.Emails = _ => Task.FromResult(Harness.BrokenBody());
        }

        var context = Context();

        await harness.Reader(false).HandleAsync(context);

        AssertRefused(context);
        AssertTokenGone(context);
        AssertRevokedOnce(harness);
        Assert.Equal(document == "emails", harness.Calls.Any(call => call.Uri.AbsolutePath.EndsWith("/emails", StringComparison.Ordinal)));
        Assert.Empty(harness.Logs.Entries);
    }

    [Fact]
    public async Task A_body_cut_while_the_request_itself_is_aborted_is_not_swallowed_and_the_token_is_still_revoked()   // the filter is the cancellation filter's
    {
        var harness = new Harness();
        using var aborted = new CancellationTokenSource();
        harness.User = _ => Task.FromResult(Harness.BrokenBody(aborted.Cancel));
        var context = Context(aborted: aborted.Token);

        await Assert.ThrowsAsync<IOException>(() => harness.Reader(false).HandleAsync(context).AsTask());

        AssertTokenGone(context);
        AssertRevokedOnce(harness);
    }

    [Fact]
    public async Task Each_call_has_a_bound_of_its_own_and_two_slow_answers_inside_it_are_read()   // finding 4
    {
        var harness = new Harness();
        harness.User = cancellationToken => harness.AfterAsync(TimeSpan.FromSeconds(6), UserJson, cancellationToken);
        harness.Emails = cancellationToken => harness.AfterAsync(TimeSpan.FromSeconds(6), EmailsJson, cancellationToken);
        var context = Context();

        var read = harness.Reader(false).HandleAsync(context).AsTask();
        await harness.WaitUntilWaitingAsync();
        harness.Clock.Advance(TimeSpan.FromSeconds(6));
        await harness.WaitUntilWaitingAsync();
        harness.Clock.Advance(TimeSpan.FromSeconds(6));
        await read.WaitAsync(Real);

        AssertHandedOver(context);
        AssertRevokedOnce(harness);
    }

    [Theory]
    [InlineData("500", true)]
    [InlineData("500", false)]
    [InlineData("unreachable", true)]
    [InlineData("unreachable", false)]
    [InlineData("timeout", true)]
    [InlineData("timeout", false)]
    public async Task A_revocation_that_fails_warns_once_without_a_secret_and_changes_nothing_else(string failure, bool goodRead)   // findings 5 and 6
    {
        var harness = new Harness();
        if (!goodRead)
        {
            harness.User = _ => Task.FromResult(Status(HttpStatusCode.InternalServerError));
        }

        harness.Revoke = failure switch
        {
            "500" => _ => Task.FromResult(Status(HttpStatusCode.InternalServerError)),
            "unreachable" => _ => throw new HttpRequestException("the connection was refused"),
            _ => harness.StallAsync,
        };
        var context = Context();

        var read = harness.Reader(false).HandleAsync(context).AsTask();
        if (failure == "timeout")
        {
            await harness.WaitUntilWaitingAsync();
            harness.Clock.Advance(GitHubReader.CallBound + TimeSpan.FromSeconds(1));
        }

        await read.WaitAsync(Real);

        if (goodRead)
        {
            AssertHandedOver(context);
        }
        else
        {
            AssertRefused(context);
            AssertTokenGone(context);
        }

        var warning = Assert.Single(harness.Logs.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(ProviderNames.GitHub, warning.Message, StringComparison.Ordinal);
        Assert.Contains(failure, warning.Message, StringComparison.Ordinal);
        AssertNoSecretLogged(harness);
        Assert.Contains(harness.Calls, call => call.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData("no token")]
    [InlineData("user 500")]
    [InlineData("user 301")]
    [InlineData("user 204")]
    [InlineData("emails 500")]
    [InlineData("emails 302")]
    [InlineData("user over 64 KiB")]
    [InlineData("emails over 64 KiB")]
    [InlineData("user without id")]
    [InlineData("user not JSON")]
    [InlineData("emails wrong entry")]
    [InlineData("emails not JSON")]
    public async Task Every_refusal_is_server_error_with_the_readers_address_and_no_secret(string path)   // finding 6
    {
        var harness = new Harness();
        switch (path)
        {
            case "user 500":
                harness.User = _ => Task.FromResult(Status(HttpStatusCode.InternalServerError));
                break;
            case "user 301":
                harness.User = _ => Task.FromResult(Status(HttpStatusCode.MovedPermanently));
                break;
            case "user 204":
                harness.User = _ => Task.FromResult(Status(HttpStatusCode.NoContent));
                break;
            case "emails 500":
                harness.Emails = _ => Task.FromResult(Status(HttpStatusCode.InternalServerError));
                break;
            case "emails 302":
                harness.Emails = _ => Task.FromResult(Status(HttpStatusCode.Found));
                break;
            case "user over 64 KiB":
                harness.User = _ => Task.FromResult(Json(PaddedUser(70_000)));
                break;
            case "emails over 64 KiB":
                harness.Emails = _ => Task.FromResult(Json(PaddedEmails(70_000)));
                break;
            case "user without id":
                harness.User = _ => Task.FromResult(Json("""{"login":"ola-gh"}"""));
                break;
            case "user not JSON":
                harness.User = _ => Task.FromResult(Json("<html>"));
                break;
            case "emails wrong entry":
                harness.Emails = _ => Task.FromResult(Json("""[{"email":5,"primary":true,"verified":true}]"""));
                break;
            case "emails not JSON":
                harness.Emails = _ => Task.FromResult(Json("not json"));
                break;
        }

        var context = Context(token: path == "no token" ? null : Token);
        await harness.Reader(false).HandleAsync(context);

        AssertRefused(context);
        Assert.Empty(harness.Logs.Entries);
        if (path == "no token")
        {
            Assert.Empty(harness.Calls);   // refused before any read, and nothing to revoke
        }
        else
        {
            AssertRevokedOnce(harness);
            AssertTokenGone(context);
        }
    }

    [Fact]
    public async Task A_token_answer_with_an_empty_token_is_refused_like_a_missing_one()   // Amendment A3
    {
        var harness = new Harness();
        var context = Context(token: string.Empty);

        await harness.Reader(false).HandleAsync(context);

        Assert.True(context.IsRejected);
        Assert.Equal(OpenIddictConstants.Errors.ServerError, context.Error);
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task The_reader_does_not_read_the_second_document_after_a_refused_first_one()   // one attempt, no retry
    {
        var harness = new Harness { User = _ => Task.FromResult(Status(HttpStatusCode.InternalServerError)) };

        await harness.Reader(false).HandleAsync(Context());

        Assert.Equal([HttpMethod.Get, HttpMethod.Delete], harness.Calls.Select(call => call.Method));
    }

    public static TheoryData<string> Untouched => new() { "other endpoint", "no registration", "no token response", "google", "microsoft" };

    [Theory]
    [MemberData(nameof(Untouched))]
    public async Task What_is_not_the_token_answer_of_the_github_callback_is_left_alone(string case_)   // finding 8
    {
        var harness = new Harness();
        var context = Context();
        switch (case_)
        {
            case "other endpoint":
                context.EndpointType = OpenIddictClientEndpointType.PostLogoutRedirection;
                break;
            case "no registration":
                context.Registration = null!;
                break;
            case "no token response":
                context.TokenResponse = null;
                break;
            default:
                context.Registration = new OpenIddictClientRegistration { ProviderName = case_, Issuer = new Uri("http://issuer.test/") };
                break;
        }

        await harness.Reader(false).HandleAsync(context);

        Assert.Empty(harness.Calls);
        Assert.False(context.IsRejected);
        Assert.Equal(Token, context.BackchannelAccessToken);
        Assert.True(context.RequireBackchannelAccessToken);
        Assert.Empty(context.Properties);
        Assert.Empty(harness.Logs.Entries);
    }
}
