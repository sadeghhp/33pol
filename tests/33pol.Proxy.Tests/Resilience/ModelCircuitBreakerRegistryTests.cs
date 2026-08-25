using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Policy.CircuitBreaker;
using Pol33.Proxy.Resilience;

namespace Pol33.Proxy.Tests.Resilience;

public sealed class ModelCircuitBreakerRegistryTests
{
    [Fact]
    public void TryEnter_AfterFailures_ReturnsFalseForModel()
    {
        var options = Options.Create(new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { CircuitBreakerFailureThreshold = 2 },
        });
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var registry = new ModelCircuitBreakerRegistry(options, metrics);

        registry.TryEnter("m1").Should().BeTrue();
        registry.RecordFailure("m1");
        registry.TryEnter("m1").Should().BeTrue();
        registry.RecordFailure("m1");

        registry.TryEnter("m1").Should().BeFalse();
        registry.GetBreaker("m1").State.Should().Be(CircuitState.Open);
        metrics.Received(1).RecordCircuitBreakerTransition("m1", "open");
    }

    [Fact]
    public void GetStates_AfterOpen_ReturnsMetricStateTwo()
    {
        var options = Options.Create(new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { CircuitBreakerFailureThreshold = 1 },
        });
        var registry = new ModelCircuitBreakerRegistry(options, Substitute.For<IGatewayMetricsCollector>());

        registry.RecordFailure("m1");

        var states = registry.GetStates();
        states.Should().ContainSingle(s => s.ModelId == "m1" && s.State == 2);
    }

    [Fact]
    public void GetStates_AfterOpen_CarriesOpenedAtAndLastTransition()
    {
        var options = Options.Create(new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { CircuitBreakerFailureThreshold = 1 },
        });
        var registry = new ModelCircuitBreakerRegistry(options, Substitute.For<IGatewayMetricsCollector>());
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        registry.RecordFailure("m1");

        var state = registry.GetStates().Single();
        state.OpenedAt.Should().NotBeNull().And.BeAfter(before);
        state.LastTransitionUtc.Should().NotBeNull().And.BeAfter(before);
    }

    [Fact]
    public void RecordFailure_WhenTrackedModelLimitReached_DoesNotGrowRegistry()
    {
        var options = Options.Create(new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions
            {
                CircuitBreakerFailureThreshold = 1,
                MaxTrackedResilienceModels = 1,
            },
        });
        var registry = new ModelCircuitBreakerRegistry(options, Substitute.For<IGatewayMetricsCollector>());

        registry.RecordFailure("m1");
        registry.RecordFailure("m2");

        var states = registry.GetStates();
        states.Should().ContainSingle(s => s.ModelId == "m1");
        states.Should().NotContain(s => s.ModelId == "m2");
    }
}
