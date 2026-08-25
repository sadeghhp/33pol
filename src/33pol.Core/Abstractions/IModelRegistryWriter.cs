using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IModelRegistryWriter
{
    Task<RegistryMutationResult> AddModelAsync(ModelConfig model, CancellationToken cancellationToken = default);

    Task<RegistryMutationResult> UpdateModelAsync(string id, ModelConfig model, CancellationToken cancellationToken = default);

    Task<RegistryMutationResult> RemoveModelAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves one route between <c>serving</c> and <c>stopped</c> (see <see cref="ModelRouteStates"/>)
    /// without touching anything else about it.
    /// </summary>
    /// <remarks>
    /// A dedicated mutation rather than a full update: the admin UI's edit path sends the whole
    /// model, so routing a stop through it would make taking a model out of service race with — and
    /// silently overwrite — a concurrent edit of its url, aliases or credential. This reads, flips
    /// and writes the one field under the same gate and route-version check as every other write.
    /// </remarks>
    Task<RegistryMutationResult> SetModelStateAsync(
        string id,
        string state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the full registry. An empty list is rejected and leaves the registry unchanged.
    /// </summary>
    Task<RegistryMutationResult> ReplaceAllAsync(
        IReadOnlyList<ModelConfig> models,
        CancellationToken cancellationToken = default);
}
