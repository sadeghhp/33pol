using Microsoft.Extensions.Diagnostics.HealthChecks;
using Pol33.Registry.Hosting;

namespace Pol33.App.Health;

/// <summary>
/// Reports stored upstream credentials that no longer decrypt under the configured key pepper.
/// </summary>
/// <remarks>
/// Degraded rather than Unhealthy on purpose: the instance still serves every model that does not
/// depend on the broken credential, so it must keep taking traffic — but the condition never fixes
/// itself and was previously visible only as one log line per restart, which is how it survived
/// nine restarts in production unnoticed.
/// </remarks>
public sealed class UpstreamSecretsHealthCheck(UpstreamSecretVerificationState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (state.Undecryptable > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"{state.Undecryptable} of {state.Total} stored upstream credential(s) cannot be decrypted "
                + "with the configured Gateway:Security:KeyPepper. Re-enter the affected models' upstream "
                + "API keys in the admin UI."));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            state.HasRun ? $"Verified {state.Total} stored upstream credential(s)." : "Not yet verified."));
    }
}
