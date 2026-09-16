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

        // Stopped routes are excluded: an operator who took a model out of service did not thereby
        // make the gateway unready, and the health sweep does not probe them either.
        var enabled = models.Where(model => model.IsServing()).ToList();

        // GetHealth, not IsBackendHealthy. The store answers two different questions and only looks
        // like one: IsBackendHealthy is deliberately optimistic for an unprobed model (it returns
        // !HealthCheckStrictMode, true by default) so warm-up traffic is not refused mid-rollout.
        // Readiness is the other question — has this backend been *proven* usable — and must never
        // be optimistic, or a pod passes its readiness gate and takes traffic before a single
        // upstream has been reached. A null verdict means "not probed yet", which is not ready.
        var probed = enabled
            .Select(model => healthStore.GetHealth(model.Id))
            .Where(health => health is not null)
            .ToList();
        var healthyCount = probed.Count(health => health!.IsHealthy);
        var draining = drainState.IsDraining;

        // An empty registry stays ready on purpose. It is an install state, not an outage, and a
        // single-replica gateway that failed its readiness probe here would be dropped from its
        // Service endpoints exactly when an operator needs the admin console to add the first
        // route. The blindness that used to hide it is fixed where it belongs: gateway_models_configured
        // is exported unconditionally, so "nothing configured" alerts instead of passing silently.
        var ready = registryLoaded &&
                    !draining &&
                    (enabled.Count == 0 || healthyCount > 0);

        return (new GatewayReadinessResponse
        {
            Status = ready ? "ready" : "not_ready",
            RegistryLoaded = registryLoaded,
            ModelCount = modelCount,
            ConfiguredBackends = enabled.Count,
            ProbedBackends = probed.Count,
            HealthyBackends = healthyCount,
            IsDraining = draining,
        }, ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
}
