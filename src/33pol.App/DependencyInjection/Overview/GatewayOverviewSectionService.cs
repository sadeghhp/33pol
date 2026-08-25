using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Core.Models.Overview;

namespace Pol33.App.DependencyInjection.Overview;

/// <summary>
/// Assembles the database-backed Overview sections and keeps the last result of each where the
/// summary (and its Attention list) can read it without blocking.
/// </summary>
/// <remarks>
/// Lives in the composition root because it is the only layer that may see billing, persistence,
/// registry and observability at once. Every section is memoised for
/// <c>Gateway:Overview:SlowSectionTtlSeconds</c>; concurrent callers share one build via a per-section
/// gate, so a burst of dashboards costs one set of queries, not one per browser.
/// </remarks>
internal sealed partial class GatewayOverviewSectionService(
    IServiceScopeFactory scopeFactory,
    IOptions<GatewayOptions> gatewayOptions,
    IOptions<BillingOptions> billingOptions,
    TimeProvider timeProvider,
    ILogger<GatewayOverviewSectionService> logger,
    IModelRegistry registry,
    IUsageWriterStateSource? usageWriter = null,
    IUsageQualityCounters? usageQuality = null,
    IBillingReconciliationStateSource? reconciliation = null,
    BudgetReservationLedger? reservations = null)
    : IOverviewSectionService, IOverviewSlowSectionCache, IOverviewHotSectionSource
{
    private readonly Section<FinOpsOverview> _finops = new();
    private readonly Section<PolicyOverview> _policy = new();
    private readonly Section<ControlPlaneOverview> _controlPlane = new();
    private readonly Section<ActivityOverview> _activity = new();
    private readonly Section<TenantsOverview> _tenants = new();

    private TimeSpan Ttl => TimeSpan.FromSeconds(Math.Max(1, gatewayOptions.Value.Overview.SlowSectionTtlSeconds));

    // ---- IOverviewSlowSectionCache ----

    public FinOpsOverview? FinOps => _finops.Last;

    public PolicyOverview? Policy => _policy.Last;

    public ControlPlaneOverview? ControlPlane => _controlPlane.Last;

    public TenantsOverview? Tenants => _tenants.Last;

    // ---- IOverviewHotSectionSource ----

    public PipelineOverview? GetPipeline()
    {
        if (usageWriter is null && usageQuality is null)
        {
            return null;
        }

        return new PipelineOverview
        {
            UsageWriterQueueDepth = usageWriter?.QueueDepth ?? -1,
            UsageWriterCapacity = usageWriter?.Capacity ?? 0,
            UsageWriterDropped = usageQuality?.DroppedEvents ?? 0,
            UsageParseFailures = usageQuality?.ParseFailures ?? 0,
            EstimatedUsage = usageQuality?.EstimatedUsage ?? 0,
            UnsplitUsage = usageQuality?.UnsplitUsage ?? 0,
        };
    }

    public PolicyLiveOverview? GetPolicy() => null;

    public ControlPlaneLiveOverview? GetControlPlane() => null;

    // ---- IOverviewSectionService ----

    public Task<FinOpsOverview?> GetFinOpsAsync(bool refresh, CancellationToken cancellationToken) =>
        _finops.GetAsync(refresh, Ttl, timeProvider, BuildFinOpsAsync, "finops", logger, cancellationToken);

    public Task<PolicyOverview?> GetPolicyAsync(bool refresh, CancellationToken cancellationToken) =>
        _policy.GetAsync(refresh, Ttl, timeProvider, BuildPolicyAsync, "policy", logger, cancellationToken);

    public Task<ControlPlaneOverview?> GetControlPlaneAsync(bool refresh, CancellationToken cancellationToken) =>
        _controlPlane.GetAsync(refresh, Ttl, timeProvider, BuildControlPlaneAsync, "control-plane", logger, cancellationToken);

    public Task<ActivityOverview?> GetActivityAsync(int limit, bool refresh, CancellationToken cancellationToken) =>
        _activity.GetAsync(refresh, Ttl, timeProvider, ct => BuildActivityAsync(limit, ct), "activity", logger, cancellationToken);

    public Task<TenantsOverview?> GetTenantsAsync(bool refresh, CancellationToken cancellationToken) =>
        _tenants.GetAsync(refresh, Ttl, timeProvider, BuildTenantsAsync, "tenants", logger, cancellationToken);

    /// <summary>Rebuilds every section; used by the background refresher to keep the cache warm.</summary>
    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        await GetFinOpsAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        await GetPolicyAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        await GetControlPlaneAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        await GetTenantsAsync(refresh: false, cancellationToken).ConfigureAwait(false);
    }

    // ---- FinOps ----

    private async Task<FinOpsOverview?> BuildFinOpsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var rollups = services.GetService<IDailyUsageRollupRepository>();
        var usage = services.GetService<IBillingUsageService>();
        var forecastService = services.GetService<IBillingForecastService>();
        if (rollups is null || usage is null || forecastService is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var yesterday = today.AddDays(-1);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var from = yesterday < monthStart ? yesterday : monthStart;
        var currency = billingOptions.Value.DefaultCurrency;

        var report = await usage.GetUsageReportAsync(new UsageReportRequest
        {
            FromDate = from,
            ToDate = today,
            TenantId = null,
            IncludeAnonymous = true,
        }, cancellationToken).ConfigureAwait(false);

        var forecast = await forecastService.GetForecastAsync(new UsageForecastRequest
        {
            Scope = UsageScope.Unrestricted,
            TrailingDays = 7,
        }, cancellationToken).ConfigureAwait(false);

        var todayRows = report.Rollups.Where(r => r.UsageDate == today).ToList();
        var mtdRows = report.Rollups.Where(r => r.UsageDate >= monthStart).ToList();

        var rateCards = services.GetService<IRateCardRepository>();
        var priced = rateCards is null
            ? new Dictionary<string, RateCardRecord>(StringComparer.OrdinalIgnoreCase)
            : await rateCards.GetActiveByModelAsync(cancellationToken).ConfigureAwait(false);
        var models = registry.GetAllModels();
        var unpriced = models
            .Select(m => m.Id)
            .Where(id => !priced.ContainsKey(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var budgets = await BuildBudgetsAsync(services, today, currency, cancellationToken).ConfigureAwait(false);

        var reconciliationStatus = reconciliation?.Current;

        return new FinOpsOverview
        {
            BuiltAtUtc = now,
            Currency = report.Currency ?? currency,
            TodayCost = todayRows.Sum(r => r.TotalCost),
            YesterdayCost = report.Rollups.Where(r => r.UsageDate == yesterday).Sum(r => r.TotalCost),
            MonthToDateCost = forecast.MonthToDateCost,
            ProjectedMonthlyCost = forecast.ProjectedMonthlyCost,
            AverageDailyCost = forecast.AverageDailyCost,
            TodayRequests = todayRows.Sum(r => (long)r.RequestCount),
            TodayPromptTokens = todayRows.Sum(r => r.PromptTokens),
            TodayCompletionTokens = todayRows.Sum(r => r.CompletionTokens),
            MonthToDateRequests = mtdRows.Sum(r => (long)r.RequestCount),
            UnpricedModelIds = unpriced,
            PricedModelCount = models.Count - unpriced.Count,
            RegisteredModelCount = models.Count,
            TopModelsMonthToDate = Top(mtdRows, r => r.ModelId),
            TopCostCentersMonthToDate = Top(mtdRows, r => string.IsNullOrEmpty(r.CostCenter) ? "(none)" : r.CostCenter),
            Reconciliation = reconciliationStatus,
            Budgets = budgets,
        };
    }

    private static IReadOnlyList<CostBreakdownRow> Top(IEnumerable<DailyUsageRollupRecord> rows, Func<DailyUsageRollupRecord, string> key, int take = 5) =>
        rows.GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CostBreakdownRow(g.Key, g.Sum(r => r.TotalCost), g.Sum(r => (long)r.RequestCount)))
            .OrderByDescending(r => r.Cost)
            .ThenByDescending(r => r.Requests)
            .Take(take)
            .ToList();

    private async Task<IReadOnlyList<BudgetStatus>> BuildBudgetsAsync(
        IServiceProvider services,
        DateOnly today,
        string currency,
        CancellationToken cancellationToken)
    {
        var budgetRepository = services.GetService<IBudgetRepository>();
        var rollups = services.GetService<IDailyUsageRollupRepository>();
        if (budgetRepository is null || rollups is null)
        {
            return [];
        }

        var budgets = await budgetRepository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (budgets.Count == 0)
        {
            return [];
        }

        var tenants = services.GetService<ITenantRepository>();
        var slugs = new Dictionary<Guid, string?>();
        var spendCache = new Dictionary<(Guid Tenant, DateOnly PeriodStart), decimal>();
        var result = new List<BudgetStatus>(budgets.Count);

        foreach (var budget in budgets)
        {
            var periodStart = BillingUsagePersistenceHandler.GetPeriodStart(today, budget.PeriodStartDay);
            if (!spendCache.TryGetValue((budget.TenantId, periodStart), out var spent))
            {
                var periodRollups = await rollups
                    .GetRollupsAsync(periodStart, today, budget.TenantId, cancellationToken)
                    .ConfigureAwait(false);
                spent = periodRollups.Sum(r => r.TotalCost);
                spendCache[(budget.TenantId, periodStart)] = spent;
            }

            if (!slugs.TryGetValue(budget.TenantId, out var slug))
            {
                slug = tenants is null
                    ? null
                    : (await tenants.GetByIdAsync(budget.TenantId, cancellationToken).ConfigureAwait(false))?.Slug;
                slugs[budget.TenantId] = slug;
            }

            var outstanding = reservations?.GetOutstanding(budget.TenantId) ?? 0m;
            var committed = spent + outstanding;
            var ratio = budget.AmountLimit <= 0 ? 0 : (double)(committed / budget.AmountLimit);

            // Breach projection at the period's own average daily rate.
            DateOnly? breach = null;
            var daysElapsed = Math.Max(1, today.DayNumber - periodStart.DayNumber + 1);
            var perDay = spent / daysElapsed;
            if (perDay > 0 && committed < budget.AmountLimit)
            {
                var daysLeft = (int)Math.Ceiling((budget.AmountLimit - committed) / perDay);
                var candidate = today.AddDays(daysLeft);
                if (candidate < periodStart.AddMonths(1))
                {
                    breach = candidate;
                }
            }

            result.Add(new BudgetStatus
            {
                BudgetId = budget.Id,
                TenantId = budget.TenantId,
                TenantSlug = slug,
                Name = budget.Name,
                Currency = string.IsNullOrEmpty(budget.Currency) ? currency : budget.Currency,
                Limit = budget.AmountLimit,
                Spent = spent,
                Outstanding = outstanding,
                Ratio = ratio,
                WarningRatio = (double)budget.WarningThresholdRatio,
                HardStopEnabled = budget.HardStopEnabled,
                PeriodStart = periodStart,
                ProjectedBreachDate = breach,
            });
        }

        result.Sort(static (a, b) => b.Ratio.CompareTo(a.Ratio));
        return result;
    }

    // ---- sections filled in by later phases ----

    private Task<PolicyOverview?> BuildPolicyAsync(CancellationToken cancellationToken) =>
        Task.FromResult<PolicyOverview?>(null);

    private Task<ControlPlaneOverview?> BuildControlPlaneAsync(CancellationToken cancellationToken) =>
        Task.FromResult<ControlPlaneOverview?>(null);

    private Task<ActivityOverview?> BuildActivityAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<ActivityOverview?>(null);

    private Task<TenantsOverview?> BuildTenantsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<TenantsOverview?>(null);

    // ---- memo ----

    /// <summary>One memoised section: last value, when it was built, and a gate so one build serves everyone.</summary>
    private sealed class Section<T> where T : class
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private T? _last;
        private DateTimeOffset _builtAt;

        public T? Last => _last;

        public async Task<T?> GetAsync(
            bool refresh,
            TimeSpan ttl,
            TimeProvider time,
            Func<CancellationToken, Task<T?>> build,
            string name,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var now = time.GetUtcNow();
            if (!refresh && _last is not null && now - _builtAt < ttl)
            {
                return _last;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                now = time.GetUtcNow();
                if (!refresh && _last is not null && now - _builtAt < ttl)
                {
                    return _last;
                }

                try
                {
                    var built = await build(cancellationToken).ConfigureAwait(false);
                    _last = built;
                    _builtAt = now;
                    return built;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A failed build keeps the previous value on the cache (stale beats blank for the
                    // attention rules) but does not extend its freshness, so the next caller retries.
                    logger.LogWarning(ex, "Building the Overview {Section} section failed", name);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
