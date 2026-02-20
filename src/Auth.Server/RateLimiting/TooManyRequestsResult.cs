using System.Globalization;
using Auth.Server.Tenancy;
using Microsoft.Net.Http.Headers;

namespace Auth.Server.RateLimiting;

/// <summary>
/// The answer of the per-IP limiter: <c>429</c>, <c>{"error":"too_many_requests","retry_after_seconds":n}</c>, <c>Retry-After: n</c>
/// and <c>Cache-Control: no-store</c>. It sets no cookie. (The lockout of spec 0003 answers <c>too_many_attempts</c>, the mail
/// limits too; this one is about the address.)
/// </summary>
public sealed class TooManyRequestsResult(int retryAfterSeconds) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var seconds = Math.Max(1, retryAfterSeconds);
        httpContext.Response.Headers[HeaderNames.CacheControl] = "no-store";
        httpContext.Response.Headers[HeaderNames.Pragma] = "no-cache";
        httpContext.Response.Headers[HeaderNames.RetryAfter] = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(new { error = TenancyErrors.TooManyRequests, retry_after_seconds = seconds }, statusCode: StatusCodes.Status429TooManyRequests)
            .ExecuteAsync(httpContext);
    }
}
