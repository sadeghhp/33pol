using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Pol33.Billing.Usage;

/// <summary>
/// Short-lived per-tenant cache of period-to-date <em>persisted</em> spend, with an explicit
/// per-tenant invalidation hook for the usage writer.
/// </summary>
/// <remarks>
/// <para>Budget enforcement reads persisted spend once per hard-stop budget per request, and each
/// read summed every rollup row in the billing period; the cache keeps that off the hot path.</para>
///
/// <para>The cache is only safe against overshoot if there is never a moment when a request's cost
/// is counted by neither the reservation ledger nor the cached spend. The usage writer releases a
/// request's reservation the instant its rollup increment commits, so it must first call
/// <see cref="Invalidate"/> for that tenant: the next enforcement read then re-sums the rollups
/// (which now include the batch) instead of serving a figure from before the batch landed. Without
/// that call every flush opened a window of one cache TTL during which a tenant's headroom was
/// overstated by the flushed batch's cost, and sustained traffic reopened it every second.</para>
///
/// <para>Invalidation is implemented as a per-tenant generation stamped into the cache key, because
/// <see cref="IMemoryCache"/> cannot remove entries by prefix and the key also carries the period
/// and day. Superseded entries simply age out under their own TTL. The generation is captured at
/// read time and handed back on <see cref="Set"/>: a read that started before a batch committed
/// (and so summed the rollups without it) must not be cached once the writer has invalidated,
/// otherwise the pre-batch figure would be served under the new generation for a full TTL.</para>
/// </remarks>
public sealed class BudgetSpendCache(IMemoryCache memoryCache)
{
    private readonly ConcurrentDictionary<Guid, long> _generations = new();

    /// <summary>
    /// Looks up the cached spend and, hit or miss, reports the tenant's invalidation generation as
    /// observed <em>now</em>. A caller that misses must hand that generation back to
    /// <see cref="Set"/> so a figure computed from a pre-invalidation read is never cached under a
    /// newer generation.
    /// </summary>
    public bool TryGet(Guid tenantId, DateOnly periodStart, DateOnly today, out decimal spend, out long generation)
    {
        generation = GetGeneration(tenantId);
        return memoryCache.TryGetValue(Key(tenantId, generation, periodStart, today), out spend);
    }

    /// <summary>
    /// Caches <paramref name="spend"/> under <paramref name="observedGeneration"/> — the generation
    /// returned by the <see cref="TryGet"/> that missed — and only if no <see cref="Invalidate"/> for
    /// the tenant has happened since. Otherwise the figure was summed before a batch landed and is
    /// dropped: the next read re-sums the rollups.
    /// </summary>
    /// <returns><see langword="true"/> if the value was cached.</returns>
    public bool Set(Guid tenantId, DateOnly periodStart, DateOnly today, decimal spend, TimeSpan ttl, long observedGeneration)
    {
        if (GetGeneration(tenantId) != observedGeneration)
        {
            return false;
        }

        // The generation is baked into the key, so even if an Invalidate lands between the check
        // above and this Set, the entry is written under the OLD generation and is unreachable to
        // subsequent reads (which key on the new one). No stale figure can be served either way.
        memoryCache.Set(Key(tenantId, observedGeneration, periodStart, today), spend, ttl);
        return true;
    }

    /// <summary>
    /// Discards every cached spend figure for <paramref name="tenantId"/>, so the next read observes
    /// the rollups as they are now. Call after spend has been committed to the rollups and before
    /// the corresponding reservations are released.
    /// </summary>
    public void Invalidate(Guid tenantId) =>
        _generations.AddOrUpdate(tenantId, static _ => 1L, static (_, current) => current + 1);

    /// <summary>Current invalidation generation for a tenant (0 until first invalidated).</summary>
    public long GetGeneration(Guid tenantId) =>
        _generations.TryGetValue(tenantId, out var generation) ? generation : 0L;

    private static string Key(Guid tenantId, long generation, DateOnly periodStart, DateOnly today) =>
        $"budget-spend:{tenantId:N}:{generation}:{periodStart:yyyy-MM-dd}:{today:yyyy-MM-dd}";
}
