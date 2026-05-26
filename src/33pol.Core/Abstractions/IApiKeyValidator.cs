using Pol33.Core.Security;

namespace Pol33.Core.Abstractions;

public interface IApiKeyValidator
{
    ApiKeyValidationResult Validate(string? apiKey, ApiKeyPolicy policy);
}
