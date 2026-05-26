using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IModelRegistryWriter
{
    Task AddModelAsync(ModelConfig model, CancellationToken cancellationToken = default);

    Task UpdateModelAsync(string id, ModelConfig model, CancellationToken cancellationToken = default);

    Task RemoveModelAsync(string id, CancellationToken cancellationToken = default);

    Task ReplaceAllAsync(IReadOnlyList<ModelConfig> models, CancellationToken cancellationToken = default);
}
