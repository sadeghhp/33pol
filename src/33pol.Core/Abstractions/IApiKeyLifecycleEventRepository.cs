using Pol33.Core.Identity;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Append-only history of API key transitions. Rows are never deleted with the key they describe —
/// see <see cref="ApiKeyLifecycleEvent"/>.
/// </summary>
public interface IApiKeyLifecycleEventRepository
{
    Task AppendAsync(ApiKeyLifecycleEventRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every recorded event for a key, oldest first. Scoped by tenant as well as id because a
    /// deleted key has no row left to check ownership against.
    /// </summary>
    Task<IReadOnlyList<ApiKeyLifecycleEventRecord>> ListForKeyAsync(
        Guid tenantId,
        Guid apiKeyId,
        CancellationToken cancellationToken = default);
}
