using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Security.Configuration;

namespace Pol33.Security.Services;

public sealed class ModelGrantService : IModelGrantService
{
    private readonly IModelGrantRepository _tenantGrants;
    private readonly IApiKeyModelGrantRepository _apiKeyGrants;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _cacheTtl;

    public ModelGrantService(
        IModelGrantRepository tenantGrants,
        IApiKeyModelGrantRepository apiKeyGrants,
        IMemoryCache cache,
        IOptions<GatewaySecurityOptions> securityOptions)
    {
        _tenantGrants = tenantGrants;
        _apiKeyGrants = apiKeyGrants;
        _cache = cache;
        _cacheTtl = TimeSpan.FromMinutes(securityOptions.Value.CacheTtlMinutes);
    }

    public async Task<bool> IsModelAllowedAsync(
        Guid tenantId,
        Guid apiKeyId,
        string canonicalModelId,
        CancellationToken cancellationToken = default)
    {
        var tenantList = await GetTenantGrantsCachedAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var keyList = await GetApiKeyGrantsCachedAsync(apiKeyId, cancellationToken).ConfigureAwait(false);
        return ModelGrantEvaluator.IsModelAllowed(tenantList, keyList, canonicalModelId);
    }

    public void InvalidateTenantGrants(Guid tenantId) =>
        _cache.Remove(TenantCacheKey(tenantId));

    public void InvalidateApiKeyGrants(Guid apiKeyId) =>
        _cache.Remove(ApiKeyCacheKey(apiKeyId));

    private async Task<IReadOnlyList<ModelGrantRecord>> GetTenantGrantsCachedAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var key = TenantCacheKey(tenantId);
        if (_cache.TryGetValue(key, out IReadOnlyList<ModelGrantRecord>? cached) && cached is not null)
        {
            return cached;
        }

        var grants = await _tenantGrants.ListByTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, grants, _cacheTtl);
        return grants;
    }

    private async Task<IReadOnlyList<ApiKeyModelGrantRecord>> GetApiKeyGrantsCachedAsync(
        Guid apiKeyId,
        CancellationToken cancellationToken)
    {
        var key = ApiKeyCacheKey(apiKeyId);
        if (_cache.TryGetValue(key, out IReadOnlyList<ApiKeyModelGrantRecord>? cached) && cached is not null)
        {
            return cached;
        }

        var grants = await _apiKeyGrants.ListByApiKeyAsync(apiKeyId, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, grants, _cacheTtl);
        return grants;
    }

    private static string TenantCacheKey(Guid tenantId) => $"model-grants:tenant:{tenantId}";

    private static string ApiKeyCacheKey(Guid apiKeyId) => $"model-grants:api-key:{apiKeyId}";
}
