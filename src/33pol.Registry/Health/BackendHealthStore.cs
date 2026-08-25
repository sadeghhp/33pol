using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Registry.Health;

public sealed class BackendHealthStore : IBackendHealthStore
{
    private readonly ConcurrentDictionary<string, BackendHealth> _health =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _strictMode;

    public BackendHealthStore(IOptions<GatewayOptions> options)
    {
        _strictMode = options.Value.HealthCheckStrictMode;
    }

    public bool IsBackendHealthy(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        if (_health.TryGetValue(modelId, out var health))
        {
            return health.IsHealthy;
        }

        return !_strictMode;
    }

    public BackendHealth? GetHealth(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return _health.TryGetValue(modelId, out var health) ? health : null;
    }

    public IReadOnlyDictionary<string, BackendHealth> GetAllHealth() =>
        new Dictionary<string, BackendHealth>(_health, StringComparer.OrdinalIgnoreCase);

    public void SetHealth(BackendHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        // Each sweep overwrites the row wholesale, so the "since when" an operator wants for an
        // unhealthy backend is carried across here: the transition stamp survives as long as the
        // state does not flip again.
        _health.AddOrUpdate(
            health.ModelId,
            static (_, incoming) => incoming with { LastTransitionUtc = incoming.LastTransitionUtc ?? incoming.LastCheckedUtc },
            static (_, previous, incoming) => incoming with
            {
                LastTransitionUtc = previous.IsHealthy == incoming.IsHealthy
                    ? previous.LastTransitionUtc ?? previous.LastCheckedUtc
                    : incoming.LastCheckedUtc,
            },
            health);
    }

    /// <summary>
    /// Forgets every model not in <paramref name="modelIds"/>. Called after each health sweep so
    /// models that were deleted or renamed stop showing stale rows in the backends view, stop being
    /// answered by <see cref="IsBackendHealthy"/> in strict mode, and stop accumulating over
    /// add/rename/delete cycles.
    /// </summary>
    public void RetainOnly(IEnumerable<string> modelIds)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        var keep = new HashSet<string>(modelIds, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _health.Keys)
        {
            if (!keep.Contains(key))
            {
                _health.TryRemove(key, out _);
            }
        }
    }
}
