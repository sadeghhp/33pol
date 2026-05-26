using Microsoft.Extensions.Options;
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
        var registry = new ModelCircuitBreakerRegistry(options);

        registry.TryEnter("m1").Should().BeTrue();
        registry.RecordFailure("m1");
        registry.TryEnter("m1").Should().BeTrue();
        registry.RecordFailure("m1");

        registry.TryEnter("m1").Should().BeFalse();
        registry.GetBreaker("m1").State.Should().Be(CircuitState.Open);
    }
}
