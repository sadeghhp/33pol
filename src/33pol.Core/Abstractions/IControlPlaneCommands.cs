using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IControlPlaneCommands
{
    Task<ConfigReloadResult> ReloadConfigAsync(CancellationToken cancellationToken = default);

    ConfigStatusResponse GetConfigStatus();

    AdminSummarySnapshot GetSummary();

    IReadOnlyList<BackendAdminDto> ListBackends();

    IReadOnlyList<ModelConfig> ListModels();

    IReadOnlyList<RecentRequestEntry> ListRecentRequests(int limit);

    Task<RegistryMutationResult> AddModelAsync(ModelConfig model, CancellationToken cancellationToken = default);

    Task<RegistryMutationResult> UpdateModelAsync(string id, ModelConfig model, CancellationToken cancellationToken = default);

    Task<RegistryMutationResult> RemoveModelAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a model route out of service (<c>stopped</c>) or puts it back (<c>serving</c>).
    /// See <see cref="ModelRouteStates"/>.
    /// </summary>
    Task<RegistryMutationResult> SetModelStateAsync(
        string id,
        string state,
        CancellationToken cancellationToken = default);
}
