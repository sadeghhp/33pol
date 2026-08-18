using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Core.Usage;

namespace Pol33.Billing.Usage;

public sealed class BillingUsageService(
    IDailyUsageRollupRepository rollups,
    IBillingEventRepository billingEvents,
    IApiKeyRepository apiKeys,
    IRateCardRepository rateCards,
    IOptions<BillingOptions> options,
    IModelRegistry registry) : IBillingUsageService
{
    public async Task<UsageReportResponse> GetUsageReportAsync(
        UsageReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<DailyUsageRollupRecord> rollupRecords;
        string source;
        if (request.ApiKeyId is Guid apiKeyId)
        {
            // The rollup table has no per-key dimension, so a per-key report is aggregated from the
            // ledger into the same buckets. Cost-centre and scope filters are applied in SQL there.
            rollupRecords = await billingEvents
                .AggregateDailyAsync(
                    new BillingEventQuery(
                        request.FromDate,
                        request.ToDate,
                        request.TenantId,
                        apiKeyId,
                        request.CostCenter,
                        IncludeAnonymous: request.IncludeAnonymous,
                        NoCostCenter: request.NoCostCenter),
                    cancellationToken)
                .ConfigureAwait(false);
            source = UsageReportSource.Events;
        }
        else
        {
            rollupRecords = await rollups
                .GetScopedRollupsAsync(request.Scope, request.FromDate, request.ToDate, cancellationToken)
                .ConfigureAwait(false);
            rollupRecords = FilterCostCenter(rollupRecords, request.CostCenter, request.NoCostCenter);
            source = UsageReportSource.Rollups;
        }

        var unpriced = await FindUnpricedModelsAsync(rollupRecords, cancellationToken).ConfigureAwait(false);

        return new UsageReportResponse
        {
            Rollups = rollupRecords,
            Summary = new UsageReportSummary
            {
                TotalPromptTokens = rollupRecords.Sum(r => r.PromptTokens),
                TotalCompletionTokens = rollupRecords.Sum(r => r.CompletionTokens),
                TotalCost = rollupRecords.Sum(r => r.TotalCost),
                TotalRequests = rollupRecords.Sum(r => r.RequestCount),
                AnonymousRequests = rollupRecords.Where(r => r.TenantId is null).Sum(r => r.RequestCount),
            },
            Currency = options.Value.DefaultCurrency,
            Source = source,
            UnpricedModelIds = unpriced,
        };
    }

    public UsageExportResult ExportRollups(
        IReadOnlyList<DailyUsageRollupRecord> rollups,
        string format) =>
        UsageExportFormatter.Format(rollups, format);

    public async Task<UsageExportResult> ExportEventsAsync(
        BillingEventQuery query,
        string format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Page through the ledger with the keyset cursor up to the export cap, then probe for one
        // more row to decide whether anything was dropped. Repositories clamp a page to
        // UsageExportLimits.MaxEventPageSize, which equals the export cap, so asking for
        // MaxEventRows + 1 in a single query could never observe the extra row: an export of more
        // than MaxEventRows silently reported itself complete.
        var events = new List<BillingEventRecord>(Math.Min(UsageExportLimits.MaxEventRows, 1024));
        BillingEventCursor? cursor = null;
        var truncated = false;
        while (true)
        {
            var remaining = UsageExportLimits.MaxEventRows - events.Count;
            if (remaining <= 0)
            {
                // At the cap: is there anything beyond it?
                var probe = await billingEvents
                    .QueryAsync(query with { Limit = 1, Cursor = cursor }, cancellationToken)
                    .ConfigureAwait(false);
                truncated = probe.Count > 0;
                break;
            }

            var pageSize = Math.Min(remaining, UsageExportLimits.MaxEventPageSize);
            var page = await billingEvents
                .QueryAsync(query with { Limit = pageSize, Cursor = cursor }, cancellationToken)
                .ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            events.AddRange(page.Count > pageSize ? page.Take(pageSize) : page);
            if (page.Count < pageSize)
            {
                break; // short page: the ledger is exhausted
            }

            cursor = BillingEventCursor.After(page, cursor);
        }

        var enriched = await AdminBillingEventMapper
            .EnrichAsync(events, apiKeys, cancellationToken)
            .ConfigureAwait(false);

        return UsageExportFormatter.FormatEvents(enriched, format, query.FromDate, query.ToDate, truncated);
    }

    public async Task<BillingEventsPage> QueryEventsAsync(
        BillingEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, UsageExportLimits.MaxEventPageSize);

        // Ask for one extra row so HasMore is exact without a second query.
        var events = await billingEvents
            .QueryAsync(query with { Limit = limit + 1 }, cancellationToken)
            .ConfigureAwait(false);
        var hasMore = events.Count > limit;
        if (hasMore)
        {
            events = events.Take(limit).ToList();
        }

        var enriched = await AdminBillingEventMapper
            .EnrichAsync(events, apiKeys, cancellationToken)
            .ConfigureAwait(false);

        return new BillingEventsPage
        {
            Events = enriched,
            Limit = limit,
            HasMore = hasMore,
            NextCursor = hasMore ? BillingEventCursor.After(events, query.Cursor)?.Encode() : null,
        };
    }

    private static IReadOnlyList<DailyUsageRollupRecord> FilterCostCenter(
        IReadOnlyList<DailyUsageRollupRecord> records,
        string? costCenter,
        bool noCostCenter)
    {
        if (noCostCenter)
        {
            return records.Where(r => string.IsNullOrWhiteSpace(r.CostCenter)).ToList();
        }

        if (string.IsNullOrWhiteSpace(costCenter))
        {
            return records;
        }

        var wanted = costCenter.Trim();
        return records
            .Where(r => string.Equals(r.CostCenter?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Models in the report that are still registered but have no active rate card — the set an
    /// operator can act on by setting pricing.
    /// </summary>
    /// <remarks>
    /// Rollups outlive models: deleting a model removes its rate card but keeps its historical rows,
    /// so a range overlapping those days would otherwise flag the retired model forever, and the
    /// "set pricing under Routing → Models" hint would point at nothing. Retired models are left
    /// out; their historical usage stays in the report at whatever cost was recorded at the time.
    /// </remarks>
    private async Task<IReadOnlyList<string>> FindUnpricedModelsAsync(
        IReadOnlyList<DailyUsageRollupRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var models = records
            .Select(r => r.ModelId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var priced = await rateCards.GetActiveByModelAsync(cancellationToken).ConfigureAwait(false);
        var lookup = new HashSet<string>(priced.Keys, StringComparer.OrdinalIgnoreCase);

        return models
            .Where(m => !lookup.Contains(m) && registry.TryGetModel(m, out _))
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
