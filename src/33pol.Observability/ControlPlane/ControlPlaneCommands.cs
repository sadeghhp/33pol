using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Observability.ControlPlane;

public sealed class ControlPlaneCommands(
    IConfigReload configReload,
    IModelRegistry registry,
    IBackendHealthStore healthStore,
    IAdminSummaryReader summaryReader,
    IRecentRequestStore recentRequestStore,
    IModelRegistryWriter registryWriter) : IControlPlaneCommands
{
    public Task<ConfigReloadResult> ReloadConfigAsync(CancellationToken cancellationToken = default) =>
        configReload.ReloadAsync(cancellationToken);

    public ConfigStatusResponse GetConfigStatus() => configReload.GetStatus();

    public AdminSummarySnapshot GetSummary() => summaryReader.GetSnapshot();

    public IReadOnlyList<BackendAdminDto> ListBackends()
    {
        var models = registry.GetAllModels();
        return models
            .Select(m =>
            {
                var health = healthStore.GetHealth(m.Id);
                return new BackendAdminDto
                {
                    ModelId = m.Id,
                    Url = m.Url,
                    // A stopped route is not probed, so its health is stale by definition; the
                    // state is what explains the missing probe.
                    IsHealthy = m.IsServing() && healthStore.IsBackendHealthy(m.Id),
                    State = m.State,
                    Alias = m.Aliases.Count > 0 ? m.Aliases[0] : null,
                    StatusCode = health?.StatusCode,
                    Error = health?.Error,
                    LastCheckedUtc = health?.LastCheckedUtc,
                    LastTransitionUtc = health?.LastTransitionUtc,
                };
            })
            .ToList();
    }

    public IReadOnlyList<ModelConfig> ListModels() => registry.GetAllModels();

    public IReadOnlyList<RecentRequestEntry> ListRecentRequests(int limit) =>
        recentRequestStore.GetRecent(limit);

    public Task<RegistryMutationResult> AddModelAsync(ModelConfig model, CancellationToken cancellationToken = default) =>
        registryWriter.AddModelAsync(model, cancellationToken);

    public Task<RegistryMutationResult> UpdateModelAsync(string id, ModelConfig model, CancellationToken cancellationToken = default) =>
        registryWriter.UpdateModelAsync(id, model, cancellationToken);

    public Task<RegistryMutationResult> RemoveModelAsync(string id, CancellationToken cancellationToken = default) =>
        registryWriter.RemoveModelAsync(id, cancellationToken);

    public Task<RegistryMutationResult> SetModelStateAsync(string id, string state, CancellationToken cancellationToken = default) =>
        registryWriter.SetModelStateAsync(id, state, cancellationToken);
}
