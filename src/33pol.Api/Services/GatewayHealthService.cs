using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Api.Services;

public sealed class GatewayHealthService(
    IModelRegistry registry,
    IBackendHealthStore healthStore,
    GatewayProcessClock processClock)
{
    public (GatewayHealthResponse Body, int StatusCode) GetHealth()
    {
        var models = registry.GetAllModels();
        var backends = new List<GatewayBackendHealthEntry>(models.Count);
        var healthyCount = 0;

        foreach (var model in models)
        {
            var isHealthy = healthStore.IsBackendHealthy(model.Id);
            if (isHealthy)
            {
                healthyCount++;
            }

            var probe = healthStore.GetHealth(model.Id);
            backends.Add(new GatewayBackendHealthEntry
            {
                ModelId = model.Id,
                Url = model.Url,
                IsHealthy = isHealthy,
                LastChecked = probe?.LastCheckedUtc,
                Error = probe?.Error,
            });
        }

        var unhealthyCount = models.Count - healthyCount;
        var status = healthyCount > 0 ? "healthy" : "degraded";
        var statusCode = healthyCount > 0 ? 200 : 503;

        return (new GatewayHealthResponse
        {
            Status = status,
            Uptime = processClock.StartedUtc,
            TotalBackends = models.Count,
            HealthyBackends = healthyCount,
            UnhealthyBackends = unhealthyCount,
            Backends = backends,
        }, statusCode);
    }
}
