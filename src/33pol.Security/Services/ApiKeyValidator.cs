using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Security;
using Pol33.Persistence.Security;
using Pol33.Security.Configuration;

namespace Pol33.Security.Services;

public sealed class ApiKeyValidator : IApiKeyValidator
{
    private readonly IApiKeyRepository _apiKeys;
    private readonly ITenantRepository _tenants;
    private readonly IMemoryCache _cache;
    private readonly GatewaySecurityOptions _options;

    public ApiKeyValidator(
        IApiKeyRepository apiKeys,
        ITenantRepository tenants,
        IMemoryCache cache,
        IOptions<GatewaySecurityOptions> options)
    {
        _apiKeys = apiKeys;
        _tenants = tenants;
        _cache = cache;
        _options = options.Value;
    }

    public async Task<ApiKeyValidationResult> ValidateAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ApiKeyValidationResult.Fail(ApiKeyValidationFailure.Missing);
        }

        var normalized = apiKey.Trim();
        var prefix = ApiKeyHashing.CreatePrefix(normalized);
        var cacheKey = $"api-key:{prefix}";

        if (_cache.TryGetValue(cacheKey, out ApiKeyValidationResult? cached) && cached is not null)
        {
            return cached;
        }

        var record = await _apiKeys.FindByPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return ApiKeyValidationResult.Fail(ApiKeyValidationFailure.Invalid);
        }

        var hash = ApiKeyHashing.Hash(normalized, _options.KeyPepper);
        if (!ApiKeyHashing.FixedTimeEquals(record.KeyHash, hash))
        {
            return ApiKeyValidationResult.Fail(ApiKeyValidationFailure.Invalid);
        }

        if (record.RevokedAt is not null)
        {
            return ApiKeyValidationResult.Fail(ApiKeyValidationFailure.Revoked);
        }

        if (record.ExpiresAt is not null && record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return ApiKeyValidationResult.Fail(ApiKeyValidationFailure.Expired);
        }

        var tenant = await _tenants.GetByIdAsync(record.TenantId, cancellationToken).ConfigureAwait(false);
        if (tenant is null || !tenant.IsActive)
        {
            return ApiKeyValidationResult.Fail(ApiKeyValidationFailure.Invalid);
        }

        var effectiveCostCenter = string.IsNullOrWhiteSpace(record.CostCenter)
            ? tenant.CostCenter
            : record.CostCenter.Trim();

        var success = ApiKeyValidationResult.Success(
            tenant.Id,
            record.Id,
            tenant.Slug,
            tenant.PlanSlug,
            effectiveCostCenter,
            record.Role);

        _cache.Set(cacheKey, success, TimeSpan.FromMinutes(_options.CacheTtlMinutes));
        _cache.Set($"api-key-id:{record.Id}", cacheKey, TimeSpan.FromMinutes(_options.CacheTtlMinutes));

        return success;
    }

    public void InvalidateCache(Guid apiKeyId)
    {
        if (_cache.TryGetValue($"api-key-id:{apiKeyId}", out string? cacheKey) && cacheKey is not null)
        {
            _cache.Remove(cacheKey);
            _cache.Remove($"api-key-id:{apiKeyId}");
        }
    }
}
