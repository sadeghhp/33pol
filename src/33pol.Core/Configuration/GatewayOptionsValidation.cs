namespace Pol33.Core.Configuration;

public static class GatewayOptionsValidation
{
    public static IReadOnlyList<string> Validate(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ModelsConfigPath))
        {
            errors.Add($"{nameof(GatewayOptions.ModelsConfigPath)} must be a non-empty path.");
        }

        if (options.ConfigReloadIntervalSeconds is < 1 or > 300)
        {
            errors.Add($"{nameof(GatewayOptions.ConfigReloadIntervalSeconds)} must be between 1 and 300 seconds.");
        }

        if (options.HealthCheckIntervalSeconds < 1)
        {
            errors.Add($"{nameof(GatewayOptions.HealthCheckIntervalSeconds)} must be at least 1 second.");
        }

        if (options.HealthCheckUnhealthyThreshold < 1)
        {
            errors.Add($"{nameof(GatewayOptions.HealthCheckUnhealthyThreshold)} must be at least 1.");
        }

        if (options.HealthCheckTimeoutSeconds < 1)
        {
            errors.Add($"{nameof(GatewayOptions.HealthCheckTimeoutSeconds)} must be at least 1 second.");
        }

        if (options.Resilience.ForwardTimeoutSeconds < 1)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.ForwardTimeoutSeconds)} must be at least 1 second.");
        }

        if (options.Resilience.StreamIdleTimeoutSeconds < 1)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.StreamIdleTimeoutSeconds)} must be at least 1 second.");
        }

        if (options.Resilience.ForwardTimeoutSecondsPerRequestMegabyte < 0)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.ForwardTimeoutSecondsPerRequestMegabyte)} cannot be negative.");
        }

        if (options.Resilience.MaxForwardTimeoutSeconds < options.Resilience.ForwardTimeoutSeconds)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.MaxForwardTimeoutSeconds)} must be at least {nameof(GatewayResilienceOptions.ForwardTimeoutSeconds)}.");
        }

        if (options.Resilience.MaxRequestBodyBytes < 1)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.MaxRequestBodyBytes)} must be at least 1 byte.");
        }

        if (options.Resilience.MaxConcurrentForwardsPerModel < 1)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.MaxConcurrentForwardsPerModel)} must be at least 1.");
        }

        if (options.Resilience.MaxQueuedForwardsPerModel < 0)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.MaxQueuedForwardsPerModel)} must be 0 or greater.");
        }

        if (options.Resilience.BulkheadQueueTimeoutSeconds < 1)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.BulkheadQueueTimeoutSeconds)} must be at least 1.");
        }

        if (options.Resilience.UpstreamConnectTimeoutSeconds < 1)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.UpstreamConnectTimeoutSeconds)} must be at least 1.");
        }

        if (options.Resilience.UpstreamPooledConnectionLifetimeSeconds < 0)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.UpstreamPooledConnectionLifetimeSeconds)} must be 0 or greater.");
        }

        if (options.Resilience.UpstreamPooledConnectionIdleTimeoutSeconds < 1)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.UpstreamPooledConnectionIdleTimeoutSeconds)} must be at least 1.");
        }

        if (options.Resilience.UpstreamMaxConnectionsPerServer < 0)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.UpstreamMaxConnectionsPerServer)} must be 0 or greater.");
        }

        if (options.Resilience.MaxTrackedResilienceModels < 1)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.MaxTrackedResilienceModels)} must be at least 1.");
        }

        if (options.Resilience.CircuitBreakerFailureThreshold < 1)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.CircuitBreakerFailureThreshold)} must be at least 1.");
        }

        if (options.Resilience.CircuitBreakerBreakDurationSeconds < 1)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.CircuitBreakerBreakDurationSeconds)} must be at least 1 second.");
        }

        if (options.Resilience.CircuitBreakerHalfOpenProbeTimeoutSeconds < 1)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.CircuitBreakerHalfOpenProbeTimeoutSeconds)} must be at least 1 second.");
        }

        if (options.Resilience.CircuitBreakerSamplingWindowSeconds < 1)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.CircuitBreakerSamplingWindowSeconds)} must be at least 1 second.");
        }

        // A ratio above 1 (e.g. "50" meaning percent) can never be reached, which silently disables
        // the breaker; 0 or below makes it purely count-based.
        if (!(options.Resilience.CircuitBreakerFailureRatioThreshold > 0d) ||
            options.Resilience.CircuitBreakerFailureRatioThreshold > 1d ||
            double.IsNaN(options.Resilience.CircuitBreakerFailureRatioThreshold))
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.CircuitBreakerFailureRatioThreshold)} must be greater than 0 and at most 1 (a fraction, not a percentage).");
        }

        if (options.Resilience.ShutdownDrainSeconds < 0)
        {
            errors.Add($"{nameof(GatewayOptions.Resilience)}.{nameof(GatewayResilienceOptions.ShutdownDrainSeconds)} must be 0 or greater.");
        }

        errors.AddRange(options.ForwardedHeaders.Validate());

        return errors;
    }

    public static bool IsValid(GatewayOptions options, out IReadOnlyList<string> errors)
    {
        errors = Validate(options);
        return errors.Count == 0;
    }
}
