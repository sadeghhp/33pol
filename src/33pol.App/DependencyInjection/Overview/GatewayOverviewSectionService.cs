using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Core.Models.Overview;
using Pol33.Observability.Policy;
using Pol33.Observability.Runtime;
using Pol33.Registry.Services;

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
    BudgetReservationLedger? reservations = null,
    PolicyPressureTracker? policyTracker = null,
    IQuotaUsageSnapshotSource? quotaUsage = null,
    IGatewayConfigProvider? configProvider = null,
    IConfigReload? configReload = null,
    FileUpstreamSecretStore? secretStore = null,
    IAuditLogReader? auditReader = null,
    GatewayRuntimeState? runtimeState = null)
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

    public PolicyLiveOverview? GetPolicy() => policyTracker?.Snapshot();

    private ControlPlaneLiveOverview? _processMemo;
    private long _processMemoSecond;

    /// <summary>Process facts, sampled at most once per second — GC info is not free and the summary is built on every frame.</summary>
    public ControlPlaneLiveOverview? GetControlPlane()
    {
        var second = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var memo = _processMemo;
        if (memo is not null && Volatile.Read(ref _processMemoSecond) == second)
        {
            return memo;
        }

        var gc = GC.GetGCMemoryInfo();
        ThreadPool.GetAvailableThreads(out var workerAvailable, out _);
        ThreadPool.GetMaxThreads(out var workerMax, out _);
        var status = configReload?.GetStatus();
        var built = new ControlPlaneLiveOverview
        {
            WorkingSetBytes = Environment.WorkingSet,
            GcHeapBytes = gc.HeapSizeBytes,
            GcCommittedBytes = gc.TotalCommittedBytes,
            Gen2Collections = GC.CollectionCount(2),
            GcPauseTimePercent = gc.PauseTimePercentage,
            ThreadPoolPendingWorkItems = ThreadPool.PendingWorkItemCount,
            ThreadPoolThreads = Math.Max(0, workerMax - workerAvailable),
            ProcessorCount = Environment.ProcessorCount,
            CpuPercent = null,
            ConfigLastReloadUtc = status?.LastReload,
            ConfigHotReloadEnabled = status?.HotReloadEnabled ?? false,
            ModelCount = status?.ModelCount ?? registry.GetAllModels().Count,
        };
        _processMemo = built;
        Volatile.Write(ref _processMemoSecond, second);
        return built;
    }

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

    // ---- Policy ----

    private async Task<PolicyOverview?> BuildPolicyAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var quotas = new List<QuotaStatus>();
        if (quotaUsage is not null)
        {
            var quota = configProvider?.Current.Quota;
            var limit = quota?.DefaultMonthlyTokenLimit ?? 0;
            var softRatio = quota?.SoftLimitRatio ?? 0.9;
            var period = now.ToString("yyyy-MM");
            var slugs = new Dictionary<string, (string? Slug, string? Plan)>(StringComparer.OrdinalIgnoreCase);

            await using var scope = scopeFactory.CreateAsyncScope();
            var tenants = scope.ServiceProvider.GetService<ITenantRepository>();

            foreach (var usage in quotaUsage.ExportUsage())
            {
                if (!string.Equals(usage.Period, period, StringComparison.Ordinal))
                {
                    continue;
                }

                string? slug = null;
                string? plan = null;
                if (tenants is not null && Guid.TryParse(usage.PartitionKey, out var tenantId))
                {
                    if (!slugs.TryGetValue(usage.PartitionKey, out var known))
                    {
                        var tenant = await tenants.GetByIdAsync(tenantId, cancellationToken).ConfigureAwait(false);
                        known = (tenant?.Slug, tenant?.PlanSlug);
                        slugs[usage.PartitionKey] = known;
                    }

                    (slug, plan) = known;
                }

                var ratio = limit > 0 ? (double)usage.Used / limit : 0;
                quotas.Add(new QuotaStatus
                {
                    PartitionKey = usage.PartitionKey,
                    TenantSlug = slug,
                    PlanSlug = plan,
                    Period = usage.Period,
                    Used = usage.Used,
                    Limit = limit,
                    Ratio = ratio,
                    NearLimit = limit > 0 && ratio >= softRatio && ratio < 1,
                    Exceeded = limit > 0 && ratio >= 1,
                });
            }

            quotas.Sort(static (a, b) => b.Ratio != a.Ratio ? b.Ratio.CompareTo(a.Ratio) : b.Used.CompareTo(a.Used));
            if (quotas.Count > 20)
            {
                quotas.RemoveRange(20, quotas.Count - 20);
            }
        }

        var budgetsNearLimit = (_finops.Last?.Budgets ?? [])
            .Where(b => b.Ratio >= Math.Min(b.WarningRatio <= 0 ? 1 : b.WarningRatio, gatewayOptions.Value.Overview.Attention.BudgetNearLimitRatio))
            .ToList();

        return new PolicyOverview
        {
            BuiltAtUtc = now,
            Quotas = quotas,
            BudgetsNearLimit = budgetsNearLimit,
            GrantDenials = policyTracker?.GrantDenials(1440) ?? [],
            UnknownModels = policyTracker?.UnknownModels(1440) ?? [],
        };
    }

    // ---- Control plane ----

    private async Task<ControlPlaneOverview?> BuildControlPlaneAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbInfo = services.GetService<ISqliteDatabaseInfo>();
        var stateStore = services.GetService<IMaintenanceStateStore>();

        var secrets = new SecretsVerificationStatus();
        if (secretStore is not null)
        {
            try
            {
                var (total, undecryptable) = secretStore.VerifyStoredSecrets();
                secrets = new SecretsVerificationStatus { HasRun = true, Total = total, Undecryptable = undecryptable, CheckedAtUtc = now };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Upstream secret verification failed while building the Overview");
            }
        }

        var database = new DatabaseStatus
        {
            Configured = dbInfo?.IsSqliteFile ?? false,
            Path = dbInfo?.DatabasePath,
            SizeBytes = dbInfo?.SizeBytes,
            JournalMode = dbInfo is { IsSqliteFile: true } ? await dbInfo.GetJournalModeAsync(cancellationToken).ConfigureAwait(false) : null,
        };

        BackupStatus? lastBackup = null;
        if (stateStore is not null)
        {
            lastBackup = await stateStore.GetAsync<BackupStatus>(MaintenanceStateKeys.LastBackup, cancellationToken).ConfigureAwait(false);
        }

        var backupCount = 0;
        if (dbInfo?.BackupDirectory is { } backupDir && Directory.Exists(backupDir))
        {
            var files = Directory.GetFiles(backupDir, "gateway-*.db");
            backupCount = files.Length;
            // No recorded state (older gateway, or the row was lost): the newest file on disk is the
            // next best answer, and better than claiming no backup exists.
            if (lastBackup is null && files.Length > 0)
            {
                var newest = files.Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).First();
                lastBackup = new BackupStatus
                {
                    AttemptedAtUtc = new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero),
                    Succeeded = true,
                    Path = newest.FullName,
                    SizeBytes = newest.Length,
                    IntegrityCheck = "unknown",
                };
            }
        }

        DateTimeOffset? auditNewest = null;
        if (auditReader is { IsAvailable: true })
        {
            try
            {
                auditNewest = (await auditReader.ReadRecentAsync(1, cancellationToken).ConfigureAwait(false)).NewestUtc;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Audit trail could not be read while building the Overview");
            }
        }

        var status = configReload?.GetStatus();
        return new ControlPlaneOverview
        {
            BuiltAtUtc = now,
            Secrets = secrets,
            LastBackup = lastBackup,
            BackupCount = backupCount,
            Database = database,
            AuditLastEntryUtc = auditNewest,
            ConfigLastReloadUtc = status?.LastReload,
            ModelCount = status?.ModelCount ?? registry.GetAllModels().Count,
        };
    }

    // ---- Activity ----

    private async Task<ActivityOverview?> BuildActivityAsync(int limit, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (auditReader is null)
        {
            return null;
        }

        if (!auditReader.IsAvailable)
        {
            return new ActivityOverview { BuiltAtUtc = now, Available = false };
        }

        var read = await auditReader.ReadRecentAsync(limit, cancellationToken).ConfigureAwait(false);

        await using var scope = scopeFactory.CreateAsyncScope();
        var tenants = scope.ServiceProvider.GetService<ITenantRepository>();
        var keys = scope.ServiceProvider.GetService<IApiKeyRepository>();
        var slugs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var entries = new List<ActivityEntry>(read.Entries.Count);
        foreach (var e in read.Entries)
        {
            string? slug = null;
            if (e.TenantId is not null && tenants is not null && Guid.TryParse(e.TenantId, out var tenantId))
            {
                if (!slugs.TryGetValue(e.TenantId, out slug))
                {
                    slug = (await tenants.GetByIdAsync(tenantId, cancellationToken).ConfigureAwait(false))?.Slug;
                    slugs[e.TenantId] = slug;
                }
            }

            string? label = null;
            if (e.ApiKeyId is not null && keys is not null && Guid.TryParse(e.ApiKeyId, out var keyId))
            {
                if (!labels.TryGetValue(e.ApiKeyId, out label))
                {
                    var key = await keys.GetByIdAsync(keyId, cancellationToken).ConfigureAwait(false);
                    label = key is null ? null : (string.IsNullOrEmpty(key.Label) ? key.KeyPrefix : key.Label);
                    labels[e.ApiKeyId] = label;
                }
            }

            entries.Add(new ActivityEntry
            {
                TimestampUtc = e.TimestampUtc,
                Action = e.Action,
                TenantId = e.TenantId,
                TenantSlug = slug,
                ApiKeyId = e.ApiKeyId,
                ApiKeyLabel = label,
                Details = e.Details,
            });
        }

        return new ActivityOverview
        {
            BuiltAtUtc = now,
            Entries = entries,
            ParseErrors = read.ParseErrors,
            Available = true,
        };
    }

    // ---- Tenants ----

    private async Task<TenantsOverview?> BuildTenantsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var tenants = services.GetService<ITenantRepository>();
        var keys = services.GetService<IApiKeyRepository>();
        var rollups = services.GetService<IDailyUsageRollupRepository>();
        if (tenants is null || keys is null)
        {
            return null;
        }

        var attention = gatewayOptions.Value.Overview.Attention;
        var tenantList = await tenants.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var byId = tenantList.ToDictionary(t => t.Id);
        var (total, revoked, archived) = await keys.CountAsync(cancellationToken).ConfigureAwait(false);
        var expiring = await keys.ListExpiringAsync(now.AddDays(attention.KeyExpiringWithinDays), cancellationToken).ConfigureAwait(false);
        var idle = await keys.ListIdleAsync(now.AddDays(-attention.KeyIdleAfterDays), cancellationToken).ConfigureAwait(false);

        KeySummary Summarise(Pol33.Core.Identity.ApiKeyRecord k) => new()
        {
            Id = k.Id,
            KeyPrefix = k.KeyPrefix,
            Label = k.Label,
            TenantSlug = byId.TryGetValue(k.TenantId, out var t) ? t.Slug : null,
            CreatedAt = k.CreatedAt,
            ExpiresAt = k.ExpiresAt,
            LastUsedAt = k.LastUsedAt,
        };

        var consumers = new List<TenantConsumer>();
        var anonymousShare = 0d;
        if (rollups is not null)
        {
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var rows = await rollups.GetScopedRollupsAsync(UsageScope.Unrestricted, monthStart, today, cancellationToken).ConfigureAwait(false);
            var allRequests = rows.Sum(r => (long)r.RequestCount);
            var anonymousRequests = rows.Where(r => r.TenantId is null).Sum(r => (long)r.RequestCount);
            anonymousShare = allRequests == 0 ? 0 : (double)anonymousRequests / allRequests;
            var recent = runtimeState?.TenantRequests.Top(now, 1440, 1000).ToDictionary(r => r.Key, r => r.Count, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            consumers = rows
                .GroupBy(r => r.TenantId)
                .Select(g => new TenantConsumer
                {
                    TenantId = g.Key,
                    TenantSlug = g.Key is { } id && byId.TryGetValue(id, out var t) ? t.Slug : (g.Key is null ? "anonymous" : null),
                    PlanSlug = g.Key is { } pid && byId.TryGetValue(pid, out var pt) ? pt.PlanSlug : null,
                    Requests = g.Sum(r => (long)r.RequestCount),
                    PromptTokens = g.Sum(r => r.PromptTokens),
                    CompletionTokens = g.Sum(r => r.CompletionTokens),
                    Cost = g.Sum(r => r.TotalCost),
                    Requests24h = g.Key is { } rid && recent.TryGetValue(rid.ToString(), out var c) ? c : 0,
                })
                .OrderByDescending(c => c.Cost)
                .ThenByDescending(c => c.Requests)
                .Take(10)
                .ToList();
        }

        return new TenantsOverview
        {
            BuiltAtUtc = now,
            Currency = billingOptions.Value.DefaultCurrency,
            TenantCount = tenantList.Count,
            KeyCount = total,
            RevokedKeyCount = revoked,
            ArchivedKeyCount = archived,
            TopConsumersMonthToDate = consumers,
            ExpiringKeys = expiring.Select(Summarise).ToList(),
            IdleKeys = idle.Select(Summarise).ToList(),
            AnonymousRequestShare = anonymousShare,
        };
    }

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
