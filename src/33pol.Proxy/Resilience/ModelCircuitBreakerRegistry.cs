using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Policy.CircuitBreaker;

namespace Pol33.Proxy.Resilience;

public sealed class ModelCircuitBreakerRegistry : ICircuitBreakerStateSource
{
    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTransition = new(StringComparer.Ordinal);
    private readonly CircuitBreakerPolicyOptions _policyOptions;
    private readonly int _maxTrackedModels;
    private readonly IGatewayMetricsCollector _metrics;
    private readonly ILogger<ModelCircuitBreakerRegistry> _logger;

    public ModelCircuitBreakerRegistry(
        IOptions<GatewayOptions> options,
        IGatewayMetricsCollector metrics,
        ILogger<ModelCircuitBreakerRegistry>? logger = null)
    {
        _policyOptions = CircuitBreakerPolicyOptions.FromGatewayResilience(options.Value.Resilience);
        _maxTrackedModels = options.Value.Resilience.MaxTrackedResilienceModels;
        _metrics = metrics;
        _logger = logger ?? NullLogger<ModelCircuitBreakerRegistry>.Instance;
        _overflowBreaker = new CircuitBreaker(_policyOptions);
    }

    /// <summary>
    /// Shared breaker used once the per-model registry is at its cardinality limit.
    /// </summary>
    /// <remarks>
    /// Overflow used to return a brand-new breaker on every call. Since every operation —
    /// <see cref="TryEnter"/>, <see cref="RecordSuccess"/>, <see cref="RecordFailure"/> — goes
    /// through <see cref="GetBreaker"/>, each got its own instance: admission always succeeded and
    /// recorded failures mutated an object that was garbage before the next request. Past the limit
    /// the breaker silently stopped protecting anything at all. A single shared instance keeps the
    /// cardinality bound while still tripping, degrading to coarser granularity rather than to off.
    /// </remarks>
    private readonly CircuitBreaker _overflowBreaker;

    public CircuitBreaker GetBreaker(string modelId)
    {
        if (_breakers.TryGetValue(modelId, out var existing))
        {
            return existing;
        }

        if (_breakers.Count >= _maxTrackedModels)
        {
            if (_overflowWarned.TrySet())
            {
                _logger.LogWarning(
                    "Circuit-breaker registry reached its limit of {MaxTrackedModels} models; further models "
                    + "share one breaker. Raise Gateway:Resilience:MaxTrackedResilienceModels if this is a "
                    + "legitimately large registry.",
                    _maxTrackedModels);
            }

            return _overflowBreaker;
        }

        return _breakers.GetOrAdd(modelId, static (_, policy) => new CircuitBreaker(policy), _policyOptions);
    }

    /// <summary>
    /// Removes tracking for a model that no longer exists, so live registry churn cannot walk the
    /// dictionary up to its cardinality limit and force every model into the shared overflow breaker.
    /// </summary>
    public void Forget(string modelId)
    {
        _breakers.TryRemove(modelId, out _);
        _lastTransition.TryRemove(modelId, out _);
    }

    /// <summary>Drops tracking for every model outside <paramref name="knownModelIds"/>.</summary>
    public void RetainOnly(IReadOnlySet<string> knownModelIds)
    {
        foreach (var key in _breakers.Keys)
        {
            if (!knownModelIds.Contains(key))
            {
                _breakers.TryRemove(key, out _);
            }
        }
    }

    private readonly OnceFlag _overflowWarned = new();

    private sealed class OnceFlag
    {
        private int _set;

        public bool TrySet() => Interlocked.Exchange(ref _set, 1) == 0;
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

    /// <summary>
    /// Releases a half-open probe permit without recording an outcome. Call this when a request that
    /// passed <see cref="TryEnter"/> ends for a reason unrelated to backend health.
    /// </summary>
    public void RecordAbandoned(string modelId)
    {
        var breaker = GetBreaker(modelId);
        var before = breaker.State;
        breaker.RecordAbandoned();
        NotifyStateChange(modelId, before, breaker.State);
    }

    public IReadOnlyList<CircuitBreakerModelState> GetStates()
    {
        var list = new List<CircuitBreakerModelState>(_breakers.Count);
        foreach (var pair in _breakers)
        {
            var snapshot = pair.Value.GetSnapshot();
            list.Add(new CircuitBreakerModelState(
                pair.Key,
                ToMetricState(snapshot.State),
                snapshot.OpenedAt,
                snapshot.FailuresInWindow,
                snapshot.OutcomesInWindow,
                _lastTransition.TryGetValue(pair.Key, out var at) ? at : null));
        }

        return list;
    }

    private void NotifyStateChange(string modelId, CircuitState before, CircuitState after)
    {
        if (before == after)
        {
            return;
        }

        _lastTransition[modelId] = DateTimeOffset.UtcNow;
        _metrics.RecordCircuitBreakerTransition(modelId, ToStateLabel(after));

        if (after == CircuitState.Open)
        {
            _logger.LogWarning(
                "Circuit breaker opened for model {ModelId} after {FailureThreshold} consecutive backend failures; " +
                "rejecting requests for {BreakDurationSeconds}s",
                modelId,
                _policyOptions.FailureThreshold,
                _policyOptions.BreakDuration.TotalSeconds);
        }
        else
        {
            _logger.LogInformation(
                "Circuit breaker for model {ModelId} transitioned {FromState} -> {ToState}",
                modelId,
                ToStateLabel(before),
                ToStateLabel(after));
        }
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
