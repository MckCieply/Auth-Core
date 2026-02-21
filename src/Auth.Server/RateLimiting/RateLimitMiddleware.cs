using Auth.Server.Audit;
using Auth.Server.Network;

namespace Auth.Server.RateLimiting;

/// <summary>
/// Counts every request under <c>/auth/</c> against the policy of its method and path, per client address, and answers
/// <c>429</c> over the limit without any other work: nothing downstream runs, so no password is evaluated and no lockout streak
/// changes. After the forwarded-headers middleware (the address is the real one) and before authentication (login is OpenIddict's,
/// inside it). A refusal is written to the audit log, at most once per partition (an address, or the /64 of an IPv6 one) and
/// policy per minute, before the answer is sent; the write never fails the request.
/// </summary>
public sealed class RateLimitMiddleware(RequestDelegate next, SlidingWindowLimiter limiter, RateLimitSettings settings, RateLimitAudit audit)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (settings.Enabled && RatePolicies.Classify(context.Request.Method, context.Request.Path) is { } policy)
        {
            var limit = settings.PermitPerMinute(policy);
            var partition = ClientAddress.PartitionOf(context);
            var decision = limiter.TryAcquire(policy, partition, limit);
            if (!decision.Allowed)
            {
                // Deduped on the limiter's partition (an IPv6 /64 is one source), so that no address inside it can multiply the rows;
                // the row still records the full address.
                if (audit.ShouldRecord(partition, policy))
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
