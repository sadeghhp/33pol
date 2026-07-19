using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Registry.Services;

/// <summary>
/// <see cref="IConfigReload"/> over the database-backed model registry: a reload re-reads the routes
/// from the database (or file when DB-less) into the in-memory registry, and status reflects the
/// currently loaded routes. Replaces the retired file-watching reload service.
/// </summary>
public sealed class ModelRegistryConfigReload(
    ModelRegistryLoader loader,
    IModelRegistry registry,
    ILogger<ModelRegistryConfigReload> logger) : IConfigReload
{
    private volatile bool _reloadInProgress;

    public bool IsReloadInProgress => _reloadInProgress;

    public async Task<ConfigReloadResult> ReloadAsync(CancellationToken cancellationToken = default)
    {
        var previous = registry.GetAllModels().Count;
        _reloadInProgress = true;
        try
        {
            await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
            var models = registry.GetAllModels();
            return ConfigReloadResult.Success(
                "Model registry reloaded.",
                previous,
                models.Count,
                models.Select(m => m.Id).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Model registry reload failed.");
            return ConfigReloadResult.Error("Model registry reload failed.");
        }
        finally
        {
            _reloadInProgress = false;
        }
    }

    public ConfigStatusResponse GetStatus()
    {
        var models = registry.GetAllModels();
        return new ConfigStatusResponse
        {
            HotReloadEnabled = true,
            WatchEnabled = false,
            LastReload = null,
            ModelCount = models.Count,
            Models = models
                .Select(m => new ConfigStatusModel { Id = m.Id, Url = m.Url, Aliases = m.Aliases })
                .ToList(),
        };
    }
}
