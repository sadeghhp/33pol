using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IModelRegistry
{
    bool TryGetModel(string name, out ModelConfig? model);

    IReadOnlyList<ModelConfig> GetAllModels();

    bool ModelExists(string name);

    /// <summary>
    /// True once a route set has been loaded successfully. An empty-but-loaded registry is a valid
    /// configured state; an empty-because-the-load-failed one is a broken gateway, and only this
    /// flag tells them apart. Defaulted for implementations that are always loaded by construction.
    /// </summary>
    bool IsLoaded => true;

    string? GetBackendUrl(string name);

    Task LoadModelsAsync(string configPath, CancellationToken cancellationToken = default);
}
