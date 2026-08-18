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
    IOptions<BillingOptions> options) : IBillingUsageService
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

        // One row past the cap tells us whether anything was dropped without a second count query.
        var probe = query with { Limit = UsageExportLimits.MaxEventRows + 1, Cursor = null };
        var events = await billingEvents.QueryAsync(probe, cancellationToken).ConfigureAwait(false);
        var truncated = events.Count > UsageExportLimits.MaxEventRows;
        if (truncated)
        {
            events = events.Take(UsageExportLimits.MaxEventRows).ToList();
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
            .Where(m => !lookup.Contains(m))
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
