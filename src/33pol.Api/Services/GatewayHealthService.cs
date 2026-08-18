using Pol33.Api.Contracts;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Api.Services;

public sealed class GatewayHealthService(
    IModelRegistry registry,
    IBackendHealthStore healthStore,
    GatewayProcessClock processClock)
{
    /// <summary>The full report: per-backend upstream URL and probe error included.</summary>
    /// <remarks>
    /// For operator callers only. The URL is the internal upstream address and the error is
    /// free text from the prober (exception messages, hostnames), so <c>/health</c> serves this
    /// shape only when the caller satisfies the Operator policy; everyone else gets
    /// <see cref="GetHealthSummary"/>.
    /// </remarks>
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

        var (status, statusCode) = Classify(healthyCount);

        return (new GatewayHealthResponse
        {
            Status = status,
            Uptime = processClock.StartedUtc,
            TotalBackends = models.Count,
            HealthyBackends = healthyCount,
            UnhealthyBackends = models.Count - healthyCount,
            Backends = backends,
        }, statusCode);
    }

    /// <summary>
    /// The anonymous shape of <c>/health</c>: overall status, counts, and per-backend up/down with
    /// no upstream URL and no probe error text.
    /// </summary>
    public (GatewayHealthSummaryResponse Body, int StatusCode) GetHealthSummary()
    {
        var models = registry.GetAllModels();
        var backends = new List<GatewayBackendHealthSummaryEntry>(models.Count);
        var healthyCount = 0;

        foreach (var model in models)
        {
            var isHealthy = healthStore.IsBackendHealthy(model.Id);
            if (isHealthy)
            {
                healthyCount++;
            }

            var probe = healthStore.GetHealth(model.Id);
            backends.Add(new GatewayBackendHealthSummaryEntry
            {
                ModelId = model.Id,
                IsHealthy = isHealthy,
                LastChecked = probe?.LastCheckedUtc,
            });
        }

        var (status, statusCode) = Classify(healthyCount);

        return (new GatewayHealthSummaryResponse
        {
            Status = status,
            Uptime = processClock.StartedUtc,
            TotalBackends = models.Count,
            HealthyBackends = healthyCount,
            UnhealthyBackends = models.Count - healthyCount,
            Backends = backends,
        }, statusCode);
    }

    private static (string Status, int StatusCode) Classify(int healthyCount) =>
        healthyCount > 0 ? ("healthy", 200) : ("degraded", 503);
}
