using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IModelRegistryWriter
{
    Task<RegistryMutationResult> AddModelAsync(ModelConfig model, CancellationToken cancellationToken = default);

    Task<RegistryMutationResult> UpdateModelAsync(string id, ModelConfig model, CancellationToken cancellationToken = default);

    Task<RegistryMutationResult> RemoveModelAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the full registry. An empty list is rejected and leaves the registry unchanged.
    /// </summary>
    Task<RegistryMutationResult> ReplaceAllAsync(
        IReadOnlyList<ModelConfig> models,
        CancellationToken cancellationToken = default);
}
