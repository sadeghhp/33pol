using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Registry.Services;

/// <summary>
/// Applies admin mutations to the model registry: validates, persists the full route set to the
/// database (<see cref="IModelRouteRepository"/>), then swaps the in-memory registry so the change is
/// live immediately. Writes are serialized by <see cref="RegistryGate"/>. Requires a configured
/// database; DB-less deployments are read-only from models.json.
/// </summary>
public sealed class ModelRegistryWriter(
    ModelRegistryService registry,
    RegistryGate gate,
    IServiceScopeFactory scopeFactory,
    IUpstreamSecretStore secretStore,
    ILogger<ModelRegistryWriter> logger) : IModelRegistryWriter
{
    public async Task<RegistryMutationResult> AddModelAsync(
        ModelConfig model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.Url))
            {
                return RegistryMutationResult.Fail("Model id and url are required.");
            }

            if (!ModelConfigValidation.TryValidate(model, out var validationError))
            {
                return RegistryMutationResult.Fail(validationError!);
            }

            if (registry.ModelExists(model.Id))
            {
                return RegistryMutationResult.Fail($"Model '{model.Id}' already exists.", 409);
            }

            var models = registry.GetAllModels().ToList();
            models.Add(ModelRegistryPersistence.CloneModel(model));

            return await PersistAndApplyAsync(models, $"Model '{model.Id}' added.", "add model", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RegistryMutationResult> UpdateModelAsync(
        string id,
        ModelConfig model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(model);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!registry.TryGetModel(id, out var existing) || existing is null)
            {
                return RegistryMutationResult.Fail($"Model '{id}' was not found.", 404);
            }

            if (!ModelConfigValidation.TryValidate(model, out var validationError))
            {
                return RegistryMutationResult.Fail(validationError!);
            }

            var canonicalId = existing.Id;
            var updated = ModelRegistryPersistence.CloneModel(model);
            updated.Id = canonicalId;

            var models = registry.GetAllModels()
                .Select(m => string.Equals(m.Id, canonicalId, StringComparison.OrdinalIgnoreCase) ? updated : m)
                .ToList();

            return await PersistAndApplyAsync(models, $"Model '{canonicalId}' updated.", "update model", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RegistryMutationResult> RemoveModelAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!registry.TryGetModel(id, out var existing) || existing is null)
            {
                return RegistryMutationResult.Fail($"Model '{id}' was not found.", 404);
            }

            var canonicalId = existing.Id;
            var models = registry.GetAllModels()
                .Where(m => !string.Equals(m.Id, canonicalId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (models.Count == 0)
            {
                logger.LogWarning("Remove of {ModelId} would clear registry; rejected.", canonicalId);
                return RegistryMutationResult.Fail(
                    "Cannot remove the last model; registry would be empty.",
                    400);
            }

            var result = await PersistAndApplyAsync(models, $"Model '{canonicalId}' removed.", "remove model", cancellationToken)
                .ConfigureAwait(false);
            if (result.Success)
            {
                await secretStore.DeleteAsync(canonicalId, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RegistryMutationResult> ReplaceAllAsync(
        IReadOnlyList<ModelConfig> models,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (models.Count == 0)
            {
                logger.LogWarning("ReplaceAll rejected: empty model list would clear the registry.");
                return RegistryMutationResult.Fail(
                    "Cannot replace registry with an empty model list.",
                    400);
            }

            foreach (var model in models)
            {
                if (!ModelConfigValidation.TryValidate(model, out var validationError))
                {
                    return RegistryMutationResult.Fail(validationError!);
                }
            }

            var cloned = models.Select(ModelRegistryPersistence.CloneModel).ToList();
            return await PersistAndApplyAsync(
                    cloned,
                    $"Registry replaced with {cloned.Count} model(s).",
                    "replace registry",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RegistryMutationResult> PersistAndApplyAsync(
        IReadOnlyList<ModelConfig> models,
        string successMessage,
        string action,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetService<IModelRouteRepository>();
        if (repository is null)
        {
            return RegistryMutationResult.Fail("Model registry updates require a configured database.", 503);
        }

        try
        {
            await repository.ReplaceAllAsync(models, cancellationToken).ConfigureAwait(false);
            registry.Apply(models);
            logger.LogInformation("Registry {Action}: {ModelCount} model(s).", action, models.Count);
            return RegistryMutationResult.Ok(successMessage);
        }
        catch (Exception ex)
        {
            return ModelRegistryPersistErrors.FromException(ex, "database", logger, action);
        }
    }
}
