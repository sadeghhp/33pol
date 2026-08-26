using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Registry.Services;

public sealed class ModelRegistryService : IModelRegistry
{
    private readonly ILogger<ModelRegistryService> _logger;

    /// <summary>Serialises writers only; readers never take it.</summary>
    private readonly object _writeLock = new();

    /// <summary>
    /// The live routing table, replaced wholesale rather than edited in place.
    /// </summary>
    /// <remarks>
    /// Every inference request resolves its model through <see cref="TryGetModel"/>, and the admin
    /// Overview calls <see cref="GetAllModels"/> on every live-stream frame. Both used to take a
    /// process-wide lock, so model resolution serialised across all concurrent requests and behind
    /// the console's rendering. The set is only ever swapped for a freshly built one — nothing
    /// mutates a published lookup or list — so a single volatile reference gives readers a
    /// consistent snapshot with no lock at all.
    /// </remarks>
    private volatile Snapshot _snapshot = Snapshot.Empty;

    private sealed record Snapshot(
        Dictionary<string, ModelConfig> Lookup,
        List<ModelConfig> Models,
        bool IsLoaded,
        long AppliedRouteVersion)
    {
        public static readonly Snapshot Empty =
            new(new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase), [], false, 0);
    }

    public ModelRegistryService(ILogger<ModelRegistryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// True once a model set has been loaded or applied successfully. Distinguishes "no routes are
    /// configured" (a valid state) from "the routes could not be read" (a broken gateway) — readiness
    /// depends on the difference, and a model count alone cannot tell them apart.
    /// </summary>
    public bool IsLoaded => _snapshot.IsLoaded;

    /// <summary>The route-table version the current in-memory set was built from; 0 when unknown.</summary>
    public long AppliedRouteVersion => _snapshot.AppliedRouteVersion;

    public bool TryGetModel(string name, out ModelConfig? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!_snapshot.Lookup.TryGetValue(name, out var found))
        {
            model = null;
            return false;
        }

        // A copy, like GetAllModels: ModelConfig is mutable (Url, Aliases, UpstreamAuth) and a
        // caller normalising the result must not edit the live routing table behind the
        // writer's back.
        model = ModelRegistryPersistence.CloneModel(found);
        return true;
    }

    public IReadOnlyList<ModelConfig> GetAllModels() =>
        _snapshot.Models.Select(ModelRegistryPersistence.CloneModel).ToList();

    public bool ModelExists(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _snapshot.Lookup.ContainsKey(name);
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
        // The lock orders concurrent writers; readers see the new table on the volatile write.
        lock (_writeLock)
        {
            _snapshot = new Snapshot(snapshot.Lookup, snapshot.Models, IsLoaded: true, routeVersion);
        }
    }
}
