using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.App.DependencyInjection;

/// <summary>
/// Logs, once at startup, every gateway-side limit that can turn "the GPU is idle" into "clients
/// are being refused or made to wait", so an operator investigating throughput reads the effective
/// ceilings in the first screen of the log instead of reconstructing them from three config
/// sections and the admin UI.
/// </summary>
/// <remarks>
/// The rate-limit tier is read from the live snapshot, which — when a database is configured — is
/// the value seeded on first boot and edited since through the admin UI, <em>not</em> whatever is
/// currently in <c>appsettings.json</c>. That divergence is the usual reason a deployment is more
/// tightly limited than its config file suggests, and is why the message spells out where the
/// numbers came from.
/// </remarks>
internal sealed class GatewayAdmissionLimitsStartupLogger(
    IOptions<GatewayOptions> options,
    IGatewayConfigProvider configProvider,
    IHostEnvironment environment,
    ILogger<GatewayAdmissionLimitsStartupLogger> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var resilience = options.Value.Resilience;
        var rateLimits = configProvider.Current.RateLimits;
        var tier = rateLimits.Default;

        logger.LogInformation(
            "Admission limits: per-model bulkhead {MaxConcurrent} in flight + {MaxQueued} queued "
            + "(queue timeout {QueueTimeout}s); rate limiting {RateLimitState}, default tier "
            + "{Rpm} rpm + {Burst} burst, {MaxStreams} concurrent streams per partition "
            + "(partition = tenant for API-key traffic, remote address for anonymous traffic). "
            + "Rate-limit values come from the live config snapshot (database when configured), "
            + "editable under Admin → Rate limits.",
            resilience.MaxConcurrentForwardsPerModel,
            resilience.MaxQueuedForwardsPerModel,
            resilience.BulkheadQueueTimeoutSeconds,
            rateLimits.Enabled ? "enabled" : "DISABLED",
            tier.Rpm,
            tier.Burst,
            tier.MaxConcurrentStreams <= 0 ? "unlimited" : tier.MaxConcurrentStreams.ToString());

        // The two controls that can refuse a model's traffic wholesale, for reasons unrelated to how
        // many requests are in flight. Both cost availability when they misfire, so their effective
        // values belong beside the concurrency ceilings rather than only in the config file.
        logger.LogInformation(
            "Backend availability controls: circuit breaker opens after {FailureThreshold} failures at "
            + "{FailureRatio:P0} of outcomes in {SamplingWindow}s, stays open {BreakDuration}s, then admits one "
            + "probe whose permit is reclaimed after {HalfOpenProbeTimeout}s if it has not reported; health "
            + "sweep every {HealthInterval}s at {HealthTimeout}s per probe, marking a backend down after "
            + "{UnhealthyThreshold} consecutive failed sweeps and restoring it on the first success. "
            + "A backend that is down or breaker-open is refused at admission, so these decide how a slow "
            + "model server degrades: gracefully, or into blanket 503s.",
            resilience.CircuitBreakerFailureThreshold,
            resilience.CircuitBreakerFailureRatioThreshold,
            resilience.CircuitBreakerSamplingWindowSeconds,
            resilience.CircuitBreakerBreakDurationSeconds,
            resilience.CircuitBreakerHalfOpenProbeTimeoutSeconds,
            options.Value.HealthCheckIntervalSeconds,
            options.Value.HealthCheckTimeoutSeconds,
            options.Value.HealthCheckUnhealthyThreshold);

        if (rateLimits.Enabled &&
            tier.MaxConcurrentStreams > 0 &&
            tier.MaxConcurrentStreams < resilience.MaxConcurrentForwardsPerModel)
        {
            logger.LogWarning(
                "The default rate-limit tier allows only {MaxStreams} concurrent streams per partition, "
                + "below the per-model bulkhead of {MaxConcurrent}. Every API key issued from the admin "
                + "console belongs to the same tenant and therefore shares ONE partition, so this — not "
                + "the GPU — is the ceiling on simultaneous streaming requests for the whole deployment. "
                + "Raise MaxConcurrentStreams (or set it to 0 to defer to the bulkhead) under "
                + "Admin → Rate limits if the model server is being under-used.",
                tier.MaxConcurrentStreams,
                resilience.MaxConcurrentForwardsPerModel);
        }

        if (!options.Value.ForwardedHeaders.Enabled && !environment.IsDevelopment())
        {
            logger.LogInformation(
                "Forwarded headers are disabled: behind a reverse proxy or ingress every anonymous "
                + "caller is seen with the proxy's address and shares one rate-limit partition. Set "
                + "Gateway:ForwardedHeaders:Enabled=true with KnownProxies/KnownNetworks if anonymous "
                + "(publicAccess) traffic arrives through a proxy.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
