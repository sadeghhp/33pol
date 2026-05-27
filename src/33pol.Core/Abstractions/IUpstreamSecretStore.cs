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
}
