using Auth.Server.Network;

namespace Auth.Server.RateLimiting;

/// <summary>
/// Counts every request under <c>/auth/</c> against the policy of its method and path, per client address, and answers
/// <c>429</c> over the limit without any other work: nothing downstream runs, so no password is evaluated and no lockout streak
/// changes. After the forwarded-headers middleware (the address is the real one) and before authentication (login is OpenIddict's,
/// inside it).
/// </summary>
public sealed class RateLimitMiddleware(RequestDelegate next, SlidingWindowLimiter limiter, RateLimitSettings settings)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (settings.Enabled && RatePolicies.Classify(context.Request.Method, context.Request.Path) is { } policy)
        {
            var decision = limiter.TryAcquire(policy, ClientAddress.PartitionOf(context), settings.PermitPerMinute(policy));
            if (!decision.Allowed)
            {
                await new TooManyRequestsResult(decision.RetryAfterSeconds).ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}
