using Microsoft.Extensions.Diagnostics.HealthChecks;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Registry.Services;

namespace Pol33.Registry.Health;

/// <summary>
/// Reports stored upstream credentials that no longer decrypt under the configured key pepper.
/// </summary>
/// <remarks>
/// <para>Verifies live on every probe rather than caching the startup result: the remedy is to
/// re-enter the key in the admin UI, and a cached verdict would keep reporting Degraded until the
/// next restart — after the operator had already fixed it. The check decrypts a handful of small
/// entries, which is negligible at probe cadence.</para>
///
/// <para>Degraded rather than Unhealthy on purpose: the instance still serves every model that does
/// not depend on the broken credential, so it must keep taking traffic — but the condition never
/// fixes itself and was previously visible only as one log line per restart, which is how it
/// survived nine restarts in production unnoticed.</para>
/// </remarks>
public sealed class UpstreamSecretsHealthCheck(
    FileUpstreamSecretStore secretStore,
    IGatewayErrorRecorder? errorRecorder = null) : IHealthCheck
{
    private const string Remedy =
        "Re-enter the affected models' upstream API keys in the admin UI.";

    private int _lastUndecryptable;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var (total, undecryptable) = secretStore.VerifyStoredSecrets();

        if (undecryptable > 0)
        {
            var message =
                $"{undecryptable} of {total} stored upstream credential(s) cannot be decrypted with the "
                + "configured Gateway:Security:KeyPepper. " + Remedy;

            // Recorded when the condition appears (or worsens), not on every probe: it is one fault
            // until an operator fixes it, and the Errors tab is its durable history.
            if (undecryptable > Interlocked.Exchange(ref _lastUndecryptable, undecryptable))
            {
                errorRecorder?.Record(new GatewayErrorRecord
                {
                    Id = $"err_{Guid.NewGuid():N}",
                    Fingerprint = string.Empty,
                    OccurredAt = DateTimeOffset.UtcNow,
                    Level = GatewayLogLevel.Critical.ToString(),
                    Source = GatewayErrorSourceNames.Health,
                    Category = nameof(UpstreamSecretsHealthCheck),
                    EventCode = "secrets_undecryptable",
                    Message = message,
                    Outcome = "secrets_undecryptable",
                    Hint = Remedy,
                });
            }

            return Task.FromResult(HealthCheckResult.Degraded(message));
        }

        Interlocked.Exchange(ref _lastUndecryptable, 0);
        return Task.FromResult(HealthCheckResult.Healthy(
            $"Verified {total} stored upstream credential(s)."));
    }
}
