using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Api.Services;

public sealed class GatewayReadinessService(
    IConfigReload configReload,
    IModelRegistry registry,
    IBackendHealthStore healthStore,
    IGatewayDrainState drainState)
{
    public (GatewayReadinessResponse Body, int StatusCode) GetReadiness()
    {
        var configStatus = configReload.GetStatus();
        var models = registry.GetAllModels();
        var modelCount = models.Count;
        // Loaded-ness comes from the registry itself, not from the model count: an operator who has
        // deleted their last route has an empty registry that is perfectly healthy, while a registry
        // left empty by a failed load must never report ready.
        var registryLoaded = registry.IsLoaded && !configReload.IsReloadInProgress;

        var healthyCount = models.Count(model => healthStore.IsBackendHealthy(model.Id));
        var draining = drainState.IsDraining;

        var ready = registryLoaded &&
                    !draining &&
                    (modelCount == 0 || healthyCount > 0);

        return (new GatewayReadinessResponse
        {
            Status = ready ? "ready" : "not_ready",
            RegistryLoaded = registryLoaded,
            ModelCount = modelCount,
            HealthyBackends = healthyCount,
            IsDraining = draining,
        }, ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
}
