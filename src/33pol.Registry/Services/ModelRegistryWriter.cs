using System.Text.Json;
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
    private readonly RegistryGate _gate;
    private readonly GatewayOptions _options;
    private readonly ILogger<ModelRegistryWriter> _logger;

    public ModelRegistryWriter(
        ModelRegistryService registry,
        RegistryGate gate,
        IOptions<GatewayOptions> options,
        ILogger<ModelRegistryWriter> logger)
    {
        _registry = registry;
        _gate = gate;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RegistryMutationResult> AddModelAsync(
        ModelConfig model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.Url))
            {
                return RegistryMutationResult.Fail("Model id and url are required.");
            }

            if (_registry.ModelExists(model.Id))
            {
                return RegistryMutationResult.Fail($"Model '{model.Id}' already exists.", 409);
            }

            var models = _registry.GetAllModels().ToList();
            models.Add(ModelRegistryPersistence.CloneModel(model));

            var configPath = ResolveConfigPath();
            await _registry.PersistAndApplyAsync(configPath, models, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Added model {ModelId} to registry.", model.Id);

            return RegistryMutationResult.Ok($"Model '{model.Id}' added.");
        }
        catch (Exception ex)
        {
            return ModelRegistryPersistErrors.FromException(ex, ResolveConfigPath(), _logger, "add model");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RegistryMutationResult> UpdateModelAsync(
        string id,
        ModelConfig model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(model);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_registry.TryGetModel(id, out var existing) || existing is null)
            {
                return RegistryMutationResult.Fail($"Model '{id}' was not found.", 404);
            }

            var canonicalId = existing.Id;
            var updated = ModelRegistryPersistence.CloneModel(model);
            updated.Id = canonicalId;

            var models = _registry.GetAllModels()
                .Select(m => string.Equals(m.Id, canonicalId, StringComparison.OrdinalIgnoreCase) ? updated : m)
                .ToList();

            if (models.All(m => !string.Equals(m.Id, canonicalId, StringComparison.OrdinalIgnoreCase)))
            {
                return RegistryMutationResult.Fail($"Model '{id}' was not found.", 404);
            }

            var configPath = ResolveConfigPath();
            await _registry.PersistAndApplyAsync(configPath, models, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated model {ModelId} in registry.", canonicalId);

            return RegistryMutationResult.Ok($"Model '{canonicalId}' updated.");
        }
        catch (Exception ex)
        {
            return ModelRegistryPersistErrors.FromException(ex, ResolveConfigPath(), _logger, "update model");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RegistryMutationResult> RemoveModelAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_registry.TryGetModel(id, out var existing) || existing is null)
            {
                return RegistryMutationResult.Fail($"Model '{id}' was not found.", 404);
            }

            var canonicalId = existing.Id;
            var models = _registry.GetAllModels()
                .Where(m => !string.Equals(m.Id, canonicalId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var configPath = ResolveConfigPath();
            if (models.Count == 0)
            {
                _logger.LogWarning(
                    "Remove of {ModelId} would clear registry; persisting empty file is skipped.",
                    canonicalId);
                return RegistryMutationResult.Fail(
                    "Cannot remove the last model; registry would be empty.",
                    400);
            }

            await _registry.PersistAndApplyAsync(configPath, models, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Removed model {ModelId} from registry.", canonicalId);

            return RegistryMutationResult.Ok($"Model '{canonicalId}' removed.");
        }
        catch (Exception ex)
        {
            return ModelRegistryPersistErrors.FromException(ex, ResolveConfigPath(), _logger, "remove model");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RegistryMutationResult> ReplaceAllAsync(
        IReadOnlyList<ModelConfig> models,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (models.Count == 0)
            {
                _logger.LogWarning("ReplaceAll rejected: empty model list would clear the registry.");
                return RegistryMutationResult.Fail(
                    "Cannot replace registry with an empty model list.",
                    400);
            }

            var cloned = models.Select(ModelRegistryPersistence.CloneModel).ToList();
            var configPath = ResolveConfigPath();
            await _registry.PersistAndApplyAsync(configPath, cloned, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Replaced registry with {ModelCount} models.", cloned.Count);

            return RegistryMutationResult.Ok($"Registry replaced with {cloned.Count} model(s).");
        }
        catch (Exception ex)
        {
            return ModelRegistryPersistErrors.FromException(ex, ResolveConfigPath(), _logger, "replace registry");
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ResolveConfigPath() =>
        ModelRegistryInitializer.ResolveConfigPath(_options.ModelsConfigPath);
}
