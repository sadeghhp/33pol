namespace Pol33.Core.Abstractions;

/// <summary>
/// Stores upstream provider API keys outside the model registry file.
/// </summary>
public interface IUpstreamSecretStore
{
    bool TryGet(string modelId, out string? secret);

    Task PutAsync(string modelId, string secret, CancellationToken cancellationToken = default);

    Task DeleteAsync(string modelId, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the subset of <paramref name="modelIds"/> that have a stored secret, in one call.
    /// </summary>
    /// <remarks>
    /// Exists so callers listing many models do not fan out into one round-trip per model. A store
    /// backed by a remote secret manager can answer this with a single query; the previous per-model
    /// loop also invited the caller to block on each call in turn.
    /// </remarks>
    Task<IReadOnlySet<string>> ListExistingAsync(
        IEnumerable<string> modelIds,
        CancellationToken cancellationToken = default);
}
