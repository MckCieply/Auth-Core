using Auth.Server.Audit;
using Auth.Server.Network;

namespace Auth.Server.RateLimiting;

/// <summary>
/// Counts every request under <c>/auth/</c> against the policy of its method and path, per client address, and answers
/// <c>429</c> over the limit without any other work: nothing downstream runs, so no password is evaluated and no lockout streak
/// changes. After the forwarded-headers middleware (the address is the real one) and before authentication (login is OpenIddict's,
/// inside it). A refusal is written to the audit log, at most once per address and policy per minute, before the answer is sent;
/// the write never fails the request.
/// </summary>
public sealed class RateLimitMiddleware(RequestDelegate next, SlidingWindowLimiter limiter, RateLimitSettings settings, RateLimitAudit audit)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (settings.Enabled && RatePolicies.Classify(context.Request.Method, context.Request.Path) is { } policy)
        {
            var limit = settings.PermitPerMinute(policy);
            var decision = limiter.TryAcquire(policy, ClientAddress.PartitionOf(context), limit);
            if (!decision.Allowed)
            {
                if (audit.ShouldRecord(ClientAddress.Text(context), policy))
                {
                    await context.RequestServices.GetRequiredService<AuditLog>().WriteAloneAsync(
                        new AuditEntry
                        {
                            Kind = AuditKinds.RateLimitHit,
                            Details = new Dictionary<string, object?> { ["policy"] = RatePolicies.NameOf(policy), ["limit"] = limit },
                        },
                        cancellationToken: CancellationToken.None);
                }

                await new TooManyRequestsResult(decision.RetryAfterSeconds).ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}
