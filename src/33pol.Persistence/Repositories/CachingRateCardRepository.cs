using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;

namespace Pol33.Persistence.Repositories;

/// <summary>
/// Read-through cache over <see cref="RateCardRepository"/>, following the same
/// cache-with-explicit-invalidation-on-write shape as ApiKeyValidator and ModelGrantService.
///
/// This exists because QuotaMiddleware prices every request before forwarding it
/// (BillingBudgetEnforcementService.EstimateMaxCostAsync), so an uncached lookup is one SQLite
/// query per inference call.
///
/// Only "current" lookups are cached. A caller asking for a historical instant bypasses the cache
/// entirely, so back-dated pricing questions are always answered from the database.
/// Invalidation is in-process only; the TTL bounds staleness for multi-replica deployments.
/// </summary>
public sealed class CachingRateCardRepository(
    RateCardRepository inner,
    IMemoryCache cache,
    IOptions<BillingOptions> billingOptions) : IRateCardRepository
{
    private const string KeyPrefix = "rate-card:";

    private TimeSpan Ttl => TimeSpan.FromSeconds(Math.Max(1, billingOptions.Value.RateCardCacheTtlSeconds));

    /// <summary>
    /// Case-folded, because the storage layer matches model ids case-insensitively (NOCASE
    /// collation) and the registry resolves them with OrdinalIgnoreCase. A raw-cased key would give
    /// "GPT-4o" and "gpt-4o" separate cache entries for the same row, so an admin price change
    /// invalidated only the casing that happened to be used on the write path and the other casing
    /// kept serving the stale price until its TTL expired.
    /// </summary>
    private static string CacheKey(string modelId) =>
        KeyPrefix + modelId.Trim().ToLowerInvariant();

    public async Task<RateCardRecord?> GetActiveForModelAsync(
        string modelId,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        var ttl = Ttl;

        // Anything outside the TTL window around "now" is a historical query; answer it from the
        // database so a cached current price is never mistaken for a past one.
        if (DateTimeOffset.UtcNow - atUtc > ttl || atUtc - DateTimeOffset.UtcNow > ttl)
        {
            return await inner
                .GetActiveForModelAsync(modelId, atUtc, cancellationToken)
                .ConfigureAwait(false);
        }

        var key = CacheKey(modelId);
        if (cache.TryGetValue<CacheEntry>(key, out var cached) && cached is not null)
        {
            return cached.Record;
        }

        var record = await inner
            .GetActiveForModelAsync(modelId, atUtc, cancellationToken)
            .ConfigureAwait(false);

        // Unpriced models are cached too: they are the common case, and leaving them uncached
        // would mean every request for an unpriced model still hits the database.
        cache.Set(key, new CacheEntry(record), ttl);
        return record;
    }

    public Task<RateCardRecord?> GetForModelAsync(
        string modelId,
        CancellationToken cancellationToken = default) =>
        GetActiveForModelAsync(modelId, DateTimeOffset.UtcNow, cancellationToken);

    public Task<IReadOnlyDictionary<string, RateCardRecord>> GetActiveByModelAsync(
        CancellationToken cancellationToken = default) =>
        // Admin list view only; not on the inference path, so it always reads through.
        inner.GetActiveByModelAsync(cancellationToken);

    public async Task UpsertForModelAsync(
        string modelId,
        decimal inputPricePerMillionTokens,
        decimal outputPricePerMillionTokens,
        CancellationToken cancellationToken = default)
    {
        await inner
            .UpsertForModelAsync(modelId, inputPricePerMillionTokens, outputPricePerMillionTokens, cancellationToken)
            .ConfigureAwait(false);

        cache.Remove(CacheKey(modelId));
    }

    public async Task DeleteForModelAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        await inner.DeleteForModelAsync(modelId, cancellationToken).ConfigureAwait(false);
        cache.Remove(CacheKey(modelId));
    }

    /// <summary>Wrapper so a cached "no rate card" is distinguishable from a cache miss.</summary>
    private sealed record CacheEntry(RateCardRecord? Record);
}
