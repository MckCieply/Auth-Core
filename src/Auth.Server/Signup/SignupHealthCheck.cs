using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Auth.Server.Signup;

/// <summary>
/// <c>Degraded</c> while the manifest asks for self-service sign-up and the completion address is not set (spec 0009 → Configuration).
/// The answer of <c>/auth/health</c> is still <c>200</c>; the reason is in the log, not in a public response.
/// </summary>
public sealed class SignupHealthCheck(SignupGate gate) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(gate.MissingAddress
            ? HealthCheckResult.Degraded("Sign-up is asked for by the manifest but its completion address is not set.")
            : HealthCheckResult.Healthy());
}
