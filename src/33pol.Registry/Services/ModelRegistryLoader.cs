using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Registry.Services;

/// <summary>
/// Loads the in-memory <see cref="ModelRegistryService"/> from its source of truth: the database when
/// one is configured (via <see cref="IModelRouteRepository"/>), otherwise the models.json file. Used
/// at startup, on an admin reload, and by the route reconcile poll.
/// </summary>
public sealed class ModelRegistryLoader(
    IServiceScopeFactory scopeFactory,
    ModelRegistryService registry,
    IOptions<GatewayOptions> options,
    ILogger<ModelRegistryLoader> logger)
{
    /// <summary>Reloads the registry from the database (or file when DB-less). Returns the model count.</summary>
    public async Task<int> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetService<IModelRouteRepository>();

        if (repository is not null)
        {
            var snapshot = await repository.ListWithVersionAsync(cancellationToken).ConfigureAwait(false);

            // Applied even when empty: with the database as the source of truth, "no routes" is a
            // state the operator can legitimately reach by deleting the last one, and treating it as
            // "leave the registry alone" made memory disagree with what is persisted.
            var (models, problems) = ModelRegistryPersistence.Sanitize(snapshot.Models);
            foreach (var problem in problems)
            {
                logger.LogError("Model route table contains an entry that cannot be loaded. {Problem}", problem);
            }

            registry.Apply(models, snapshot.Version);
            return models.Count;
        }

        // DB-less fallback: load from the models.json file.
        var path = ResolveConfigPath(options.Value.ModelsConfigPath);
        if (File.Exists(path))
        {
            await registry.LoadModelsAsync(path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            logger.LogWarning(
                "Models configuration file not found at {ConfigPath}; registry remains empty until reload.",
                path);
        }

        return registry.GetAllModels().Count;
    }

    public static string ResolveConfigPath(string modelsConfigPath)
    {
        var combined = Path.Combine(AppContext.BaseDirectory, modelsConfigPath);
        return File.Exists(combined) ? combined : modelsConfigPath;
    }
}
