using Pol33.Core.Security;

namespace Pol33.Core.Abstractions;

public interface IApiKeyValidator
{
    Task<ApiKeyValidationResult> ValidateAsync(string? apiKey, CancellationToken cancellationToken = default);

    void InvalidateCache(Guid apiKeyId);
}
