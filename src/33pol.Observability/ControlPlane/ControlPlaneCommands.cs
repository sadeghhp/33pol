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
                    IsHealthy = healthStore.IsBackendHealthy(m.Id),
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
}
