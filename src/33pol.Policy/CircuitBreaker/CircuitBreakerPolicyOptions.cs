using Pol33.Core.Configuration;

namespace Pol33.Policy.CircuitBreaker;

public sealed class CircuitBreakerPolicyOptions
{
    public int FailureThreshold { get; init; } = 5;

    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    public static CircuitBreakerPolicyOptions FromGatewayResilience(GatewayResilienceOptions resilience) =>
        new()
        {
            FailureThreshold = resilience.CircuitBreakerFailureThreshold,
            BreakDuration = TimeSpan.FromSeconds(resilience.CircuitBreakerBreakDurationSeconds),
        };
}
