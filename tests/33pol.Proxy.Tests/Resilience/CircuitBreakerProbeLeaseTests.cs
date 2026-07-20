using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Policy.CircuitBreaker;
using Pol33.Proxy.Resilience;

namespace Pol33.Proxy.Tests.Resilience;

public sealed class CircuitBreakerProbeLeaseTests
{
    [Fact]
    public void Dispose_WithoutOutcome_ReleasesHalfOpenProbeInsteadOfWedging()
    {
        var registry = CreateRegistry(failureThreshold: 1, breakDuration: TimeSpan.Zero);

        // Trip the breaker, then let it fall through to half-open.
        registry.RecordFailure("m1");
        registry.GetBreaker("m1").State.Should().Be(CircuitState.Open);
        registry.TryEnter("m1").Should().BeTrue();
        registry.GetBreaker("m1").State.Should().Be(CircuitState.HalfOpen);

        // A request admitted as the probe ends without reporting an outcome.
        using (new CircuitBreakerProbeLease(registry, "m1"))
        {
        }

        // Before the fix this returned false forever, 502-ing the model until restart.
        registry.TryEnter("m1").Should().BeTrue();
    }

    [Fact]
    public void Dispose_AfterRecordSuccess_DoesNotAlsoAbandon()
    {
        var registry = CreateRegistry(failureThreshold: 1, breakDuration: TimeSpan.Zero);

        using (var lease = new CircuitBreakerProbeLease(registry, "m1"))
        {
            lease.RecordSuccess();
        }

        registry.GetBreaker("m1").State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void Dispose_AfterRecordFailure_KeepsFailureAndDoesNotRestoreProbe()
    {
        var registry = CreateRegistry(failureThreshold: 1, breakDuration: TimeSpan.FromMinutes(5));

        using (var lease = new CircuitBreakerProbeLease(registry, "m1"))
        {
            lease.RecordFailure();
        }

        registry.GetBreaker("m1").State.Should().Be(CircuitState.Open);
        registry.TryEnter("m1").Should().BeFalse();
    }

    [Fact]
    public void RecordFailure_AfterRecordSuccess_IsIgnored()
    {
        var registry = CreateRegistry(failureThreshold: 1, breakDuration: TimeSpan.FromMinutes(5));

        using var lease = new CircuitBreakerProbeLease(registry, "m1");
        lease.RecordSuccess();
        lease.RecordFailure();

        registry.GetBreaker("m1").State.Should().Be(CircuitState.Closed);
    }

    private static ModelCircuitBreakerRegistry CreateRegistry(int failureThreshold, TimeSpan breakDuration)
    {
        var options = Options.Create(new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions
            {
                CircuitBreakerFailureThreshold = failureThreshold,
                CircuitBreakerBreakDurationSeconds = (int)Math.Max(0, breakDuration.TotalSeconds),
            },
        });

        return new ModelCircuitBreakerRegistry(options, Substitute.For<IGatewayMetricsCollector>());
    }
}
