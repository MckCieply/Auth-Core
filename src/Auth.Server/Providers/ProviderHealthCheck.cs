using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Auth.Server.Providers;

/// <summary>
/// <c>Degraded</c> while a provider the manifest lists is not offered, or the Microsoft secret lapses within 30 days or has lapsed (spec 0011 →
/// Configuration). The answer of <c>/auth/health</c> is still <c>200</c>; the reason is in the start's log, not in a public response.
/// </summary>
public sealed class ProviderHealthCheck(ProviderCatalog catalog) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(catalog.IsDegraded
            ? HealthCheckResult.Degraded("A listed provider is not offered, or the Microsoft client secret lapses within 30 days.")
            : HealthCheckResult.Healthy());
}
