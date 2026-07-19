using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Registry.Services;

/// <summary>
/// Loads the in-memory <see cref="ModelRegistryService"/> from its source of truth: the database when
/// one is configured (via <see cref="IModelRouteRepository"/>), otherwise the models.json file. Used
/// at startup and on an admin reload.
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
            var models = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
            if (models.Count > 0)
            {
                registry.Apply(models);
            }
            else
            {
                logger.LogWarning("No model routes found in the database; registry left unchanged.");
            }

            return registry.GetAllModels().Count;
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
