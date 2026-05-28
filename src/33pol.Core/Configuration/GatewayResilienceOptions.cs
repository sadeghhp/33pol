namespace Pol33.Core.Configuration;

public sealed class GatewayResilienceOptions
{
    public const string SectionName = "Resilience";

    public int ForwardTimeoutSeconds { get; set; } = 300;

    public long MaxRequestBodyBytes { get; set; } = 26_214_400;

    public int MaxConcurrentForwardsPerModel { get; set; } = 64;

    public int MaxTrackedResilienceModels { get; set; } = 1024;

    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    public int CircuitBreakerBreakDurationSeconds { get; set; } = 30;
}
