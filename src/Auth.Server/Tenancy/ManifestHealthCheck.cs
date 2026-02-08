using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Auth.Server.Tenancy;

/// <summary>
/// <c>Degraded</c> while the active manifest is not the product's file (spec 0005 → Manifest). The response of
/// <c>/auth/health</c> is still <c>200</c>: the service works, and the reason is in the log, not in a public response.
/// </summary>
public sealed class ManifestHealthCheck(ManifestHolder holder) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(holder.State.IsDegraded
            ? HealthCheckResult.Degraded("The manifest file was not used.")
            : HealthCheckResult.Healthy());
}
