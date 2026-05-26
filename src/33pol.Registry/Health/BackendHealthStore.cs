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
        _health[health.ModelId] = health;
    }
}
