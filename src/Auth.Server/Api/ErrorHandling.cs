using System.Data.Common;
using System.Net.Sockets;
using Auth.Server.Sessions;
using Microsoft.Net.Http.Headers;

namespace Auth.Server.Api;

/// <summary>
/// The outermost handler (spec 0008 → Unhandled errors): whatever the pipeline throws is answered here, in every environment
/// (the developer exception page included, since this catches first) with <c>500 {"error":"internal_error"}</c>, and the exception
/// is logged. Kestrel's own <c>500</c> would drop every header set so far, the security headers with them. A refresh that fails
/// because the database cannot be reached (a transient <see cref="DbException"/>, a <see cref="TimeoutException"/> or a
/// <see cref="SocketException"/> at any depth) is <c>503 {"error":"temporarily_unavailable"}</c> with <c>Retry-After: 5</c> and no
/// cookie, so that the person stays signed in (Decision 4). A request the server itself rejected while a body was read (a
/// <see cref="BadHttpRequestException"/>: too large, or malformed chunks) is answered with the server's own status (<c>413</c>,
/// <c>400</c>) and <c>{"error":"invalid_request"}</c>, logged as information: the client's fault, not ours. A request the client
/// has abandoned is not answered, and an exception after the response has started ends the connection (it is logged here once).
/// </summary>
public sealed partial class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public const string InternalError = "internal_error";
    public const string InvalidRequest = "invalid_request";
    public const string TemporarilyUnavailable = "temporarily_unavailable";
    public const int RefreshRetryAfterSeconds = 5;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away: nobody is left to answer.
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted)
            {
                // Too late to say anything: end the connection. Not rethrown, so that the server does not log it a second time.
                LogAfterStart(logger, exception);
                context.Abort();
                return;
            }

            var rejected = exception as BadHttpRequestException;
            var outage = rejected is null && RefreshRequestHandler.IsRefreshPath(context.Request.Path) && IsTransientFailure(exception);
            if (rejected is not null)
            {
                LogRejected(logger, exception);
            }
            else if (outage)
            {
                LogOutage(logger, exception);
            }
            else
            {
                LogUnhandled(logger, exception);
            }

            // Drops what the request set so far: a Set-Cookie above all, so that the cookie is neither cleared nor rotated.
            context.Response.Clear();
            context.Response.Headers[HeaderNames.CacheControl] = "no-store";
            context.Response.Headers[HeaderNames.Pragma] = "no-cache";
            if (rejected is not null)
            {
                await Results.Json(new { error = InvalidRequest }, statusCode: rejected.StatusCode).ExecuteAsync(context);
            }
            else if (outage)
            {
                context.Response.Headers[HeaderNames.RetryAfter] = RefreshRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Results.Json(new { error = TemporarilyUnavailable }, statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
            }
            else
            {
                await Results.Json(new { error = InternalError }, statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
            }
        }
    }

    /// <summary>
    /// Whether the exception, or anything it wraps (an <see cref="AggregateException"/> included), is a transient
    /// <see cref="DbException"/>, a <see cref="TimeoutException"/> or a <see cref="SocketException"/>: a dependency that is out, and
    /// not a fault of the request or of our code.
    /// </summary>
    internal static bool IsTransientFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is DbException { IsTransient: true } or TimeoutException or SocketException)
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } wrapped)
            {
                pending.Push(wrapped);
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception; answering 500 internal_error.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The server rejected the request while its body was read; answering with the server's status and invalid_request.")]
    private static partial void LogRejected(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A dependency could not be reached while refreshing a session; answering 503 temporarily_unavailable.")]
    private static partial void LogOutage(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception after the response had started; the connection is aborted.")]
    private static partial void LogAfterStart(ILogger logger, Exception exception);
}

public static class ErrorHandling
{
    /// <summary>
    /// Adds <see cref="ErrorHandlingMiddleware"/>. It must be the first of Auth-Core's own middleware, and <c>UseRouting</c> must come
    /// after it: <c>WebApplication</c> puts routing in front of everything when the app does not call it, and an exception of the
    /// route matcher would then escape the handler.
    /// </summary>
    public static IApplicationBuilder UseErrorHandling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<ErrorHandlingMiddleware>();
    }
}
