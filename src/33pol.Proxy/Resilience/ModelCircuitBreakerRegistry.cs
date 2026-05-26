using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Policy.CircuitBreaker;

namespace Pol33.Proxy.Resilience;

public sealed class ModelCircuitBreakerRegistry
{
    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new(StringComparer.Ordinal);
    private readonly CircuitBreakerPolicyOptions _policyOptions;

    public ModelCircuitBreakerRegistry(IOptions<GatewayOptions> options)
    {
        _policyOptions = CircuitBreakerPolicyOptions.FromGatewayResilience(options.Value.Resilience);
    }

    public CircuitBreaker GetBreaker(string modelId) =>
        _breakers.GetOrAdd(modelId, static (_, policy) => new CircuitBreaker(policy), _policyOptions);

    public bool TryEnter(string modelId) => GetBreaker(modelId).TryEnter();

    public void RecordSuccess(string modelId) => GetBreaker(modelId).RecordSuccess();

    public void RecordFailure(string modelId) => GetBreaker(modelId).RecordFailure();
}
