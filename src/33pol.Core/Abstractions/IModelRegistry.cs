using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IModelRegistry
{
    bool TryGetModel(string name, out ModelConfig? model);

    IReadOnlyList<ModelConfig> GetAllModels();

    bool ModelExists(string name);

    string? GetBackendUrl(string name);

    Task LoadModelsAsync(string configPath, CancellationToken cancellationToken = default);
}
