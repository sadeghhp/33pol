using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;

namespace Pol33.Persistence.Repositories;

/// <summary>
/// Read-through cache over <see cref="BudgetRepository"/>, matching the shape of
/// <see cref="CachingRateCardRepository"/>.
/// </summary>
/// <remarks>
/// Budget enforcement runs on every inference request, so an uncached lookup is a database query per
/// call before a single byte reaches the upstream. Budget definitions change on an admin-action
/// cadence, not a per-request one, so they cache well.
///
/// Only the budget <em>definitions</em> are cached — never spend, and never an allow/deny decision.
/// Spend is still read fresh on each enforcement, and the in-flight gap between a reservation and
/// its persisted cost is covered by <c>BudgetReservationLedger</c>, so caching definitions cannot
/// let a tenant exceed a hard stop.
///
/// The gateway has no in-process budget write path today, so there is no mutation hook to invalidate
/// from; <see cref="Invalidate"/> exists for when one is added, and until then the TTL is what bounds
/// staleness (including across replicas).
/// </remarks>
public sealed class CachingBudgetRepository(
    BudgetRepository inner,
    IMemoryCache cache,
    IOptions<BillingOptions> billingOptions) : IBudgetRepository
{
    private const string KeyPrefix = "budgets:tenant:";

    private TimeSpan Ttl => TimeSpan.FromSeconds(Math.Max(1, billingOptions.Value.BudgetCacheTtlSeconds));

    private static string CacheKey(Guid tenantId) => KeyPrefix + tenantId.ToString("N");

    public async Task<IReadOnlyList<BudgetRecord>> GetByTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var key = CacheKey(tenantId);
        if (cache.TryGetValue<IReadOnlyList<BudgetRecord>>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var budgets = await inner.GetByTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);

        // Tenants with no budgets are cached too — the common case, and the one that would otherwise
        // hit the database on every single request.
        cache.Set(key, budgets, Ttl);
        return budgets;
    }

    public void Invalidate(Guid tenantId) => cache.Remove(CacheKey(tenantId));
}
