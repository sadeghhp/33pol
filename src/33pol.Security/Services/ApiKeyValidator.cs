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
    private readonly ApiKeyNegativeCache _negativeCache;
    private readonly GatewaySecurityOptions _options;

    public ApiKeyValidator(
        IApiKeyRepository apiKeys,
        ITenantRepository tenants,
        IMemoryCache cache,
        IOptions<GatewaySecurityOptions> options,
        ApiKeyNegativeCache? negativeCache = null)
    {
        _apiKeys = apiKeys;
        _tenants = tenants;
        _cache = cache;
        _negativeCache = negativeCache ?? ApiKeyNegativeCache.Shared;
        _options = options.Value;
    }

    public async Task<ApiKeyValidationResult> ValidateAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ApiKeyValidationResult.Fail(ApiKeyValidationFailure.Missing);
        }

        var normalized = apiKey.Trim();
        // Cache by the full key hash, never by the (low-entropy, semi-public) prefix. A prefix-keyed
        // cache would let any key sharing a victim's prefix hit a warm success entry and be
        // authenticated as that victim without the hash ever being verified.
        var hash = ApiKeyHashing.Hash(normalized, _options.KeyPepper);
        var cacheKey = $"api-key:{hash}";

        if (_cache.TryGetValue(cacheKey, out ApiKeyValidationResult? cached) && cached is not null)
        {
            return cached;
        }

        // A key the gateway never issued is answered from the (small, short-lived) negative cache.
        // Without it every request bearing an unknown key ran a database lookup: that is the normal
        // case for public models — SDKs insist on sending some placeholder key — and a cheap
        // amplifier for anyone spraying random keys.
        if (_negativeCache.IsKnownInvalid(hash))
        {
            return ApiKeyValidationResult.Fail(ApiKeyValidationFailure.Invalid);
        }

        // Probe every prefix format the key may be stored under, then let the hash decide: prefixes are
        // neither unique nor stable across formats, so only the hash can identify the key.
        var prefixes = ApiKeyHashing.CreateLookupPrefixes(normalized);
        var candidates = await _apiKeys.FindByPrefixesAsync(prefixes, cancellationToken).ConfigureAwait(false);

        ApiKeyRecord? record = null;
        foreach (var candidate in candidates)
        {
            if (ApiKeyHashing.FixedTimeEquals(candidate.KeyHash, hash))
            {
                record = candidate;
                break;
            }
        }

        if (record is null)
        {
            _negativeCache.MarkInvalid(hash);
            return ApiKeyValidationResult.Fail(ApiKeyValidationFailure.Invalid);
        }

        // Archiving requires the key to be revoked first, so ArchivedAt should never be the deciding
        // check. It is here anyway: the coupling lives in AdminKeyService, and a credential that still
        // authenticates because a precondition was relaxed elsewhere is not a failure this layer should
        // be able to have. Reported as Revoked because that is what an archived key is to its holder.
        if (record.RevokedAt is not null || record.ArchivedAt is not null)
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
            // Reported distinctly from Invalid: the hash matched a real key, so this is a credential
            // that stopped working rather than one the gateway never issued. Anonymous-capable routes
            // rely on that difference to decide what may be ignored. A missing tenant row is an
            // orphaned key and is treated the same way — unusable, and its holder should be told.
            return ApiKeyValidationResult.Fail(ApiKeyValidationFailure.TenantInactive);
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

        // A positive entry must not outlive the key: cap the TTL at the remaining lifetime so a key
        // expiring inside the cache window stops authenticating on time rather than up to
        // CacheTtlMinutes later.
        var ttl = TimeSpan.FromMinutes(_options.CacheTtlMinutes);
        if (record.ExpiresAt is { } expiresAt)
        {
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            if (remaining < ttl)
            {
                ttl = remaining;
            }
        }

        if (ttl <= TimeSpan.Zero)
        {
            return success;
        }

        _cache.Set(cacheKey, success, ttl);
        _cache.Set($"api-key-id:{record.Id}", cacheKey, ttl);

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
