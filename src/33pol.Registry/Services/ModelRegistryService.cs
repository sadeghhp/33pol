using System.Text.Json;
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

    public ModelRegistryService(ILogger<ModelRegistryService> logger)
    {
        _logger = logger;
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

    internal void ApplyModels((Dictionary<string, ModelConfig> Lookup, List<ModelConfig> Models) snapshot)
    {
        lock (_lock)
        {
            _lookup = snapshot.Lookup;
            _models = snapshot.Models;
        }
    }

    internal async Task PersistAndApplyAsync(
        string configPath,
        IReadOnlyList<ModelConfig> models,
        CancellationToken cancellationToken)
    {
        if (models.Count == 0)
        {
            _logger.LogWarning(
                "Persist skipped for {ConfigPath}: empty model list would clear registry; keeping in-memory state.",
                configPath);
            return;
        }

        var snapshot = ModelRegistryPersistence.BuildLookup(models);
        await ModelRegistryPersistence.WriteAtomicAsync(configPath, snapshot.Models, cancellationToken)
            .ConfigureAwait(false);
        ApplyModels(snapshot);
    }
}
