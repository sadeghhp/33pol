using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Security.Configuration;

namespace Pol33.Security.Services;

/// <summary>
/// Answers "may this key use this model?" from an in-memory cache of tenant and key grants, going
/// to the database only when a cache entry is missing.
/// </summary>
/// <remarks>
/// <para>Registered as a singleton. It used to be scoped, with the two grant repositories — and
/// therefore a <c>GatewayDbContext</c> — injected into it, so every authenticated inference request
/// paid for a fresh context even though the answer came from the cache. Now a scope, and with it a
/// context, is only created on a miss.</para>
///
/// <para>Misses are single-flighted per cache key: when a hot key's entry expires, the concurrent
/// requests that all miss at once share one query rather than each running their own. Without that,
/// every TTL expiry produced a burst of identical reads proportional to the concurrency at that
/// instant.</para>
///
/// <para>Invalidation races a load in flight: a request may have read the old grants from the
/// database just before an admin replaced them and called <c>Invalidate*</c>. Removing the cache
/// entry alone would let that load then <c>Set</c> the pre-revocation list for a full TTL. Each key
/// therefore carries an invalidation generation; a load only caches its result if the generation it
/// observed before querying is still current, and any in-flight single-flight entry is dropped so
/// the next caller starts a fresh read.</para>
/// </remarks>
public sealed class ModelGrantService : IModelGrantService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _cacheTtl;
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inFlightLoads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.Ordinal);

    public ModelGrantService(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        IOptions<GatewaySecurityOptions> securityOptions)
    {
        _scopeFactory = scopeFactory;
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

    public void InvalidateTenantGrants(Guid tenantId) => Invalidate(TenantCacheKey(tenantId));

    public void InvalidateApiKeyGrants(Guid apiKeyId) => Invalidate(ApiKeyCacheKey(apiKeyId));

    private void Invalidate(string key)
    {
        // Order matters: bump the generation first so a load that completes after this point sees
        // the change and refuses to cache, then drop the entry and any in-flight load.
        _generations.AddOrUpdate(key, 1, static (_, current) => current + 1);
        _cache.Remove(key);
        _inFlightLoads.TryRemove(key, out _);
    }

    private long GenerationOf(string key) => _generations.TryGetValue(key, out var generation) ? generation : 0;

    private Task<IReadOnlyList<ModelGrantRecord>> GetTenantGrantsCachedAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        GetCachedAsync<IReadOnlyList<ModelGrantRecord>>(
            TenantCacheKey(tenantId),
            static (sp, id, ct) => sp.GetRequiredService<IModelGrantRepository>().ListByTenantAsync(id, ct),
            tenantId,
            cancellationToken);

    private Task<IReadOnlyList<ApiKeyModelGrantRecord>> GetApiKeyGrantsCachedAsync(
        Guid apiKeyId,
        CancellationToken cancellationToken) =>
        GetCachedAsync<IReadOnlyList<ApiKeyModelGrantRecord>>(
            ApiKeyCacheKey(apiKeyId),
            static (sp, id, ct) => sp.GetRequiredService<IApiKeyModelGrantRepository>().ListByApiKeyAsync(id, ct),
            apiKeyId,
            cancellationToken);

    private async Task<T> GetCachedAsync<T>(
        string key,
        Func<IServiceProvider, Guid, CancellationToken, Task<T>> load,
        Guid id,
        CancellationToken cancellationToken)
        where T : class
    {
        if (_cache.TryGetValue(key, out T? cached) && cached is not null)
        {
            return cached;
        }

        // The shared load runs on its own token: the first caller's disconnect must not fail the
        // load for everyone coalesced behind it. Each caller still observes its own cancellation
        // while it waits.
        var lazy = _inFlightLoads.GetOrAdd(
            key,
            k => new Lazy<Task<object>>(
                () => LoadAndCacheAsync(k, load, id),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var loaded = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            return (T)loaded;
        }
        finally
        {
            // Remove only our own entry; a later loader for the same key must not be evicted by a
            // straggler from this round.
            _inFlightLoads.TryRemove(new KeyValuePair<string, Lazy<Task<object>>>(key, lazy));
        }
    }

    private async Task<object> LoadAndCacheAsync<T>(
        string key,
        Func<IServiceProvider, Guid, CancellationToken, Task<T>> load,
        Guid id)
        where T : class
    {
        // Yield first so the Lazy factory returns immediately and the loading continuation does not
        // run under the dictionary's GetOrAdd.
        await Task.Yield();

        var generation = GenerationOf(key);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var value = await load(scope.ServiceProvider, id, CancellationToken.None).ConfigureAwait(false);
        if (GenerationOf(key) == generation)
        {
            _cache.Set(key, value, _cacheTtl);
        }

        // Otherwise the grants were replaced while this read was in flight: the value is still the
        // right answer for the callers already waiting on it (they raced the change), but it must
        // not become the cached truth for the next TTL.
        return value;
    }

    private static string TenantCacheKey(Guid tenantId) => $"model-grants:tenant:{tenantId}";

    private static string ApiKeyCacheKey(Guid apiKeyId) => $"model-grants:api-key:{apiKeyId}";
}
