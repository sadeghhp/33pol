using Pol33.Core.Identity;

namespace Pol33.Core.Abstractions;

public interface IApiKeyModelGrantRepository
{
    Task<IReadOnlyList<ApiKeyModelGrantRecord>> ListByApiKeyAsync(
        Guid apiKeyId,
        CancellationToken cancellationToken = default);

    Task ReplaceForApiKeyAsync(
        Guid apiKeyId,
        IReadOnlyList<string> modelPatterns,
        CancellationToken cancellationToken = default);
}
