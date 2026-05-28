using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Policy.CircuitBreaker;

namespace Pol33.Proxy.Resilience;

public sealed class ModelCircuitBreakerRegistry : ICircuitBreakerStateSource
{
    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new(StringComparer.Ordinal);
    private readonly CircuitBreakerPolicyOptions _policyOptions;
    private readonly int _maxTrackedModels;
    private readonly IGatewayMetricsCollector _metrics;

    public ModelCircuitBreakerRegistry(
        IOptions<GatewayOptions> options,
        IGatewayMetricsCollector metrics)
    {
        _policyOptions = CircuitBreakerPolicyOptions.FromGatewayResilience(options.Value.Resilience);
        _maxTrackedModels = options.Value.Resilience.MaxTrackedResilienceModels;
        _metrics = metrics;
    }

    public CircuitBreaker GetBreaker(string modelId)
    {
        if (_breakers.TryGetValue(modelId, out var existing))
        {
            return existing;
        }

        if (_breakers.Count >= _maxTrackedModels)
        {
            // Guardrail mode: do not grow registry cardinality past configured limit.
            return new CircuitBreaker(_policyOptions);
        }

        return _breakers.GetOrAdd(modelId, static (_, policy) => new CircuitBreaker(policy), _policyOptions);
    }

    public bool TryEnter(string modelId)
    {
        var breaker = GetBreaker(modelId);
        var before = breaker.State;
        var entered = breaker.TryEnter();
        NotifyStateChange(modelId, before, breaker.State);
        return entered;
    }

    public void RecordSuccess(string modelId)
    {
        var breaker = GetBreaker(modelId);
        var before = breaker.State;
        breaker.RecordSuccess();
        NotifyStateChange(modelId, before, breaker.State);
    }

    public void RecordFailure(string modelId)
    {
        var breaker = GetBreaker(modelId);
        var before = breaker.State;
        breaker.RecordFailure();
        NotifyStateChange(modelId, before, breaker.State);
    }

    public IReadOnlyList<CircuitBreakerModelState> GetStates()
    {
        var list = new List<CircuitBreakerModelState>(_breakers.Count);
        foreach (var pair in _breakers)
        {
            list.Add(new CircuitBreakerModelState(pair.Key, ToMetricState(pair.Value.State)));
        }

        return list;
    }

    private void NotifyStateChange(string modelId, CircuitState before, CircuitState after)
    {
        if (before == after)
        {
            return;
        }

        _metrics.RecordCircuitBreakerTransition(modelId, ToStateLabel(after));
    }

    internal static int ToMetricState(CircuitState state) =>
        state switch
        {
            CircuitState.Closed => 0,
            CircuitState.HalfOpen => 1,
            CircuitState.Open => 2,
            _ => 0,
        };

    internal static string ToStateLabel(CircuitState state) =>
        state switch
        {
            CircuitState.Closed => "closed",
            CircuitState.HalfOpen => "half_open",
            CircuitState.Open => "open",
            _ => "unknown",
        };
}
