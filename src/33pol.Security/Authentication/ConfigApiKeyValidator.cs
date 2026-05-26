using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Security;

namespace Pol33.Security.Authentication;

/// <summary>
/// Phase 3 interim: config-list keys (replaced by DB-backed keys in WP3.1).
/// </summary>
public sealed class ConfigApiKeyValidator : IApiKeyValidator
{
    private readonly GatewayOptions _options;

    public ConfigApiKeyValidator(IOptions<GatewayOptions> options)
    {
        _options = options.Value;
    }

    public ApiKeyValidationResult Validate(string? apiKey, ApiKeyPolicy policy)
    {
        if (!_options.IsAuthenticationEnabled)
        {
            return ApiKeyValidationResult.Success;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ApiKeyValidationResult(ApiKeyValidationStatus.Missing);
        }

        var candidates = policy == ApiKeyPolicy.Admin ? _options.AdminApiKeys : _options.ApiKeys;
        if (candidates.Count == 0)
        {
            return new ApiKeyValidationResult(ApiKeyValidationStatus.Invalid);
        }

        foreach (var candidate in candidates)
        {
            if (FixedTimeEquals(apiKey, candidate))
            {
                return ApiKeyValidationResult.Success;
            }
        }

        return new ApiKeyValidationResult(ApiKeyValidationStatus.Invalid);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
