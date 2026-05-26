using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Registry.Hosting;

namespace Pol33.Registry.Services;

public sealed class ModelRegistryWriter : IModelRegistryWriter
{
    private readonly ModelRegistryService _registry;
    private readonly GatewayOptions _options;
    private readonly RegistryGate _gate;
    private readonly ILogger<ModelRegistryWriter> _logger;

    public ModelRegistryWriter(
        ModelRegistryService registry,
        IOptions<GatewayOptions> options,
        RegistryGate gate,
        ILogger<ModelRegistryWriter> logger)
    {
        _registry = registry;
        _options = options.Value;
        _gate = gate;
        _logger = logger;
    }

    public Task AddModelAsync(ModelConfig model, CancellationToken cancellationToken = default) =>
        MutateAsync(
            config =>
            {
                ModelRegistryPersistence.ValidateModel(model);
                config.Models ??= [];

                if (config.Models.Any(m => m.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Model '{model.Id}' already exists.");
                }

                config.Models.Add(ModelRegistryPersistence.CloneModels([model])[0]);
            },
            cancellationToken);

    public Task UpdateModelAsync(string id, ModelConfig model, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return MutateAsync(
            config =>
            {
                ModelRegistryPersistence.ValidateModel(model);

                if (!model.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Model id in body must match route id.", nameof(model));
                }

                config.Models ??= [];
                var index = config.Models.FindIndex(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    throw new InvalidOperationException($"Model '{id}' was not found.");
                }

                config.Models[index] = ModelRegistryPersistence.CloneModels([model])[0];
            },
            cancellationToken);
    }

    public Task RemoveModelAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return MutateAsync(
            config =>
            {
                config.Models ??= [];
                var removed = config.Models.RemoveAll(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (removed == 0)
                {
                    throw new InvalidOperationException($"Model '{id}' was not found.");
                }
            },
            cancellationToken);
    }

    public Task ReplaceAllAsync(IReadOnlyList<ModelConfig> models, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);

        if (models.Count == 0)
        {
            _logger.LogWarning("ReplaceAll with empty models was ignored; registry unchanged.");
            return Task.CompletedTask;
        }

        foreach (var model in models)
        {
            ModelRegistryPersistence.ValidateModel(model);
        }

        return MutateAsync(
            config => config.Models = ModelRegistryPersistence.CloneModels(models),
            cancellationToken);
    }

    private async Task MutateAsync(Action<ModelRegistryConfig> mutate, CancellationToken cancellationToken)
    {
        if (!await _gate.TryEnterAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Registry mutation already in progress.");
        }

        try
        {
            var configPath = ModelRegistryInitializer.ResolveConfigPath(_options.ModelsConfigPath);
            var config = await ModelRegistryPersistence.ReadAsync(configPath, cancellationToken).ConfigureAwait(false);
            mutate(config);

            if (config.Models is null || config.Models.Count == 0)
            {
                _logger.LogWarning(
                    "Mutation would persist an empty models list at {ConfigPath}; keeping registry unchanged.",
                    configPath);
                return;
            }

            await ModelRegistryPersistence.WriteAtomicAsync(configPath, config, cancellationToken)
                .ConfigureAwait(false);
            _registry.ApplyModels(config.Models, configPath, fromFileLoad: false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
