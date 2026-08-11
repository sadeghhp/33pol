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

        errors.AddRange(options.ForwardedHeaders.Validate());

        return errors;
    }

    public static bool IsValid(GatewayOptions options, out IReadOnlyList<string> errors)
    {
        errors = Validate(options);
        return errors.Count == 0;
    }
}
