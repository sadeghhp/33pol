using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Persists the model routing table (the rows behind the in-memory <see cref="IModelRegistry"/>).
/// Registered only when a database connection string is configured.
/// </summary>
public interface IModelRouteRepository
{
    /// <summary>Returns all model routes, ordered by id.</summary>
    Task<IReadOnlyList<ModelConfig>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the entire route table with the given models and bumps the config version, in a
    /// single atomic write. Callers validate and reject empty lists before calling.
    /// </summary>
    Task ReplaceAllAsync(IReadOnlyList<ModelConfig> models, CancellationToken cancellationToken = default);
}
