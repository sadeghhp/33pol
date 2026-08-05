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
    /// Returns all model routes together with the current route version. Mutations read through
    /// this so they can write back with the version they saw.
    /// </summary>
    Task<ModelRouteSnapshot> ListWithVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads just the current route version — a cheap check for the reconcile poll.</summary>
    Task<long> GetVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the entire route table with the given models and bumps the route version, in a
    /// single atomic write. Returns the new version.
    /// </summary>
    /// <param name="expectedVersion">
    /// The version the caller based its change on. When supplied and no longer current the write is
    /// abandoned and <see cref="ModelRouteVersionConflictException"/> is thrown, rather than
    /// clobbering the other writer's routes. Pass null only for unconditional replacement.
    /// </param>
    Task<long> ReplaceAllAsync(
        IReadOnlyList<ModelConfig> models,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
}
