using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace Auth.Server.Lockout;

/// <summary>
/// The lockout response: <c>429</c> with the remaining cooldown in the body and in <c>Retry-After</c>. The same for
/// every identifier, with or without an account, and with the cache headers of the login <c>401</c>.
/// </summary>
public sealed class TooManyAttemptsResult(TimeSpan retryAfter) : IResult
{
    public const string Error = "too_many_attempts";

    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var seconds = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds));
        httpContext.Response.Headers[HeaderNames.CacheControl] = "no-store";
        httpContext.Response.Headers[HeaderNames.Pragma] = "no-cache";
        httpContext.Response.Headers[HeaderNames.RetryAfter] = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(new { error = Error, retry_after_seconds = seconds }, statusCode: StatusCodes.Status429TooManyRequests)
            .ExecuteAsync(httpContext);
    }
}
