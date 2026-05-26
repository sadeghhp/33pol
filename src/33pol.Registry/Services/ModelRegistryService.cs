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
            return _models.ToList();
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
        var config = JsonSerializer.Deserialize<ModelRegistryConfig>(json, ModelRegistryPersistence.JsonOptions)
            ?? throw new JsonException("Model registry configuration deserialized to null.");

        ApplyModels(config.Models, configPath, fromFileLoad: true);
    }

    internal void ApplyModels(IReadOnlyList<ModelConfig>? models, string source, bool fromFileLoad)
    {
        if (models is null || models.Count == 0)
        {
            _logger.LogWarning(
                "No models found in {Source}; keeping existing registry unchanged.",
                source);
            return;
        }

        var lookup = new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase);
        var snapshot = new List<ModelConfig>(models.Count);

        foreach (var model in models)
        {
            ModelRegistryPersistence.ValidateModel(model);
            snapshot.Add(model);
            lookup[model.Id] = model;

            foreach (var alias in model.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }

                lookup[alias] = model;
            }
        }

        lock (_lock)
        {
            _lookup = lookup;
            _models = snapshot;
        }

        _logger.LogInformation(
            "Applied {ModelCount} models from {Source} (fileLoad={FromFileLoad}).",
            snapshot.Count,
            source,
            fromFileLoad);
    }
}
