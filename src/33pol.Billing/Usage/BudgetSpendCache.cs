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
/// and day. Superseded entries simply age out under their own TTL.</para>
/// </remarks>
public sealed class BudgetSpendCache(IMemoryCache memoryCache)
{
    private readonly ConcurrentDictionary<Guid, long> _generations = new();

    public bool TryGet(Guid tenantId, DateOnly periodStart, DateOnly today, out decimal spend) =>
        memoryCache.TryGetValue(Key(tenantId, periodStart, today), out spend);

    public void Set(Guid tenantId, DateOnly periodStart, DateOnly today, decimal spend, TimeSpan ttl) =>
        memoryCache.Set(Key(tenantId, periodStart, today), spend, ttl);

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

    private string Key(Guid tenantId, DateOnly periodStart, DateOnly today) =>
        $"budget-spend:{tenantId:N}:{GetGeneration(tenantId)}:{periodStart:yyyy-MM-dd}:{today:yyyy-MM-dd}";
}
