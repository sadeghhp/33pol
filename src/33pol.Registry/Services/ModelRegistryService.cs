using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Registry.Services;

public sealed class ModelRegistryService : IModelRegistry
{
    private readonly ILogger<ModelRegistryService> _logger;
    private readonly object _lock = new();
    private Dictionary<string, ModelConfig> _lookup = new(StringComparer.OrdinalIgnoreCase);
    private List<ModelConfig> _models = [];
    private bool _isLoaded;
    private long _appliedRouteVersion;

    public ModelRegistryService(ILogger<ModelRegistryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// True once a model set has been loaded or applied successfully. Distinguishes "no routes are
    /// configured" (a valid state) from "the routes could not be read" (a broken gateway) — readiness
    /// depends on the difference, and a model count alone cannot tell them apart.
    /// </summary>
    public bool IsLoaded
    {
        get
        {
            lock (_lock)
            {
                return _isLoaded;
            }
        }
    }

    /// <summary>The route-table version the current in-memory set was built from; 0 when unknown.</summary>
    public long AppliedRouteVersion
    {
        get
        {
            lock (_lock)
            {
                return _appliedRouteVersion;
            }
        }
    }

    public bool TryGetModel(string name, out ModelConfig? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_lock)
        {
            return _lookup.TryGetValue(name, out model);
        }
    }

    public IReadOnlyList<ModelConfig> GetAllModels()
    {
        lock (_lock)
        {
            return _models.Select(ModelRegistryPersistence.CloneModel).ToList();
        }
    }

    public bool ModelExists(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_lock)
        {
            return _lookup.ContainsKey(name);
        }
    }

    public string? GetBackendUrl(string name)
    {
        return TryGetModel(name, out var model) ? model!.Url : null;
    }

    public async Task LoadModelsAsync(string configPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var json = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
        var config = ModelRegistryPersistence.Deserialize(json);

        if (config.Models is null || config.Models.Count == 0)
        {
            _logger.LogWarning("No models found in {ConfigPath}; keeping existing registry unchanged.", configPath);
            return;
        }

        ApplyModels(ModelRegistryPersistence.BuildLookup(config.Models));
        _logger.LogInformation("Loaded {ModelCount} models from {ConfigPath}.", config.Models.Count, configPath);
    }

    /// <summary>
    /// Swaps the in-memory registry to the given models (deep-cloned, alias lookup rebuilt).
    /// </summary>
    /// <remarks>
    /// An empty list is applied, not ignored: removing the last route is a legitimate operator
    /// action, and refusing it here meant the registry could disagree with the persisted truth.
    /// A set that fails validation throws, so callers must build the lookup before persisting.
    /// </remarks>
    public void Apply(IReadOnlyList<ModelConfig> models, long routeVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(models);

        ApplyModels(ModelRegistryPersistence.BuildLookup(models), routeVersion);
    }

    internal void ApplyModels(
        (Dictionary<string, ModelConfig> Lookup, List<ModelConfig> Models) snapshot,
        long routeVersion = 0)
    {
        lock (_lock)
        {
            _lookup = snapshot.Lookup;
            _models = snapshot.Models;
            _isLoaded = true;
            _appliedRouteVersion = routeVersion;
        }
    }
}
