using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class BillingEventRepository(GatewayDbContext dbContext) : IBillingEventRepository, IBillingEventBatchAppender
{
    public async Task<bool> TryAppendAsync(
        BillingEventRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.RequestId))
        {
            throw new ArgumentException("RequestId is required for idempotent billing events.", nameof(record));
        }

        var exists = await dbContext.BillingEvents
            .AsNoTracking()
            .AnyAsync(e => e.RequestId == record.RequestId, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
        {
            return false;
        }

        dbContext.BillingEvents.Add(BillingEntityMapper.ToEntity(record));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsDuplicateRequestId(ex))
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<BillingEventRecord>> TryAppendManyAsync(
        IReadOnlyList<BillingEventRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return [];
        }

        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.RequestId))
            {
                throw new ArgumentException("RequestId is required for idempotent billing events.", nameof(records));
            }
        }

        // One probe for the whole batch instead of one per event, then one SaveChanges — a single
        // transaction and a single WAL commit — for every new row. Duplicates within the batch itself
        // are collapsed here too, so the unique index is never asked to arbitrate the common case.
        var requestIds = records.Select(r => r.RequestId).Distinct(StringComparer.Ordinal).ToList();
        var existing = await dbContext.BillingEvents
            .AsNoTracking()
            .Where(e => requestIds.Contains(e.RequestId))
            .Select(e => e.RequestId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var seen = new HashSet<string>(existing, StringComparer.Ordinal);

        var appended = new List<BillingEventRecord>(records.Count);
        foreach (var record in records)
        {
            if (!seen.Add(record.RequestId))
            {
                continue;
            }

            dbContext.BillingEvents.Add(BillingEntityMapper.ToEntity(record));
            appended.Add(record);
        }

        if (appended.Count == 0)
        {
            return appended;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return appended;
        }
        catch (DbUpdateException ex) when (IsDuplicateRequestId(ex))
        {
            // A concurrent writer landed one of these ids between the probe and the commit (a
            // reconciliation rerun, or a second gateway instance). Fall back to the row-by-row path
            // for this batch only, so the rest of the batch is still persisted exactly once.
            dbContext.ChangeTracker.Clear();
            var fallback = new List<BillingEventRecord>(appended.Count);
            foreach (var record in appended)
            {
                if (await TryAppendAsync(record, cancellationToken).ConfigureAwait(false))
                {
                    fallback.Add(record);
                }
            }

            return fallback;
        }
    }

    public async Task<IReadOnlyList<BillingEventRecord>> QueryAsync(
        BillingEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Clamp(query.Limit, 1, 5000);
        var dbQuery = dbContext.BillingEvents.AsNoTracking();

        if (query.TenantId is not null)
        {
            dbQuery = dbQuery.Where(e => e.TenantId == query.TenantId.Value);
        }

        if (query.ApiKeyId is not null)
        {
            dbQuery = dbQuery.Where(e => e.ApiKeyId == query.ApiKeyId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.CostCenter))
        {
            var costCenter = query.CostCenter.Trim();
            dbQuery = dbQuery.Where(e => e.CostCenter == costCenter);
        }

        if (query.FromDate is not null)
        {
            var from = query.FromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            dbQuery = dbQuery.Where(e => e.RecordedAt >= from);
        }

        if (query.ToDate is not null)
        {
            var to = query.ToDate.Value.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
            dbQuery = dbQuery.Where(e => e.RecordedAt <= to);
        }

        var entities = await dbQuery
            .OrderByDescending(e => e.RecordedAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(BillingEntityMapper.ToRecord).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, ApiKeyUsageSummary>> GetUsageSummariesAsync(
        Guid tenantId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default)
    {
        var from = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = toDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        // Projected and aggregated in memory, deliberately. SQLite has no decimal type — EF stores
        // decimal as TEXT — so a server-side SUM() coerces every value to a REAL and adds them in
        // IEEE-754 double precision before handing the result back as a decimal. That silently
        // drifts money totals away from the exact sum of the underlying rows, by an amount that
        // depends on row order. DailyUsageRollupRepository sums in memory for the same reason.
        var rows = await dbContext.BillingEvents
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.ApiKeyId != null)
            .Where(e => e.RecordedAt >= from && e.RecordedAt <= to)
            .Select(e => new
            {
                ApiKeyId = e.ApiKeyId!.Value,
                e.PromptTokens,
                e.CompletionTokens,
                e.TotalCost,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .GroupBy(row => row.ApiKeyId)
            .ToDictionary(
                group => group.Key,
                group => new ApiKeyUsageSummary
                {
                    RequestCount = group.Count(),
                    PromptTokens = group.Sum(row => row.PromptTokens),
                    CompletionTokens = group.Sum(row => row.CompletionTokens),
                    TotalCost = group.Sum(row => row.TotalCost ?? 0m),
                });
    }

    public async Task<IReadOnlyList<DailyUsageRollupRecord>> GetDailyTotalsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default)
    {
        if (toDate < fromDate)
        {
            return [];
        }

        var from = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = toDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        // Streamed rather than materialised: a busy day's ledger is unbounded, and reconciliation
        // must not be the thing that exhausts memory on the box it is meant to be watching. Only the
        // per-bucket accumulator is held, which is bounded by (days x tenants x models x centres).
        var rows = dbContext.BillingEvents
            .AsNoTracking()
            .Where(e => e.RecordedAt >= from && e.RecordedAt <= to)
            .Select(e => new
            {
                e.RecordedAt,
                e.TenantId,
                e.ModelId,
                e.CostCenter,
                e.PromptTokens,
                e.CompletionTokens,
                e.TotalCost,
            })
            .AsAsyncEnumerable();

        var buckets = new Dictionary<DailyUsageRollupKey, (long Prompt, long Completion, decimal Cost, int Count)>();

        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // Bucketed identically to DailyUsageRollupKey.FromEvent — the rollup writer's own key —
            // so a difference in the report is a difference in the data, never in the grouping.
            var key = new DailyUsageRollupKey(
                DateOnly.FromDateTime(row.RecordedAt.UtcDateTime),
                row.TenantId,
                row.ModelId,
                DailyUsageRollupKey.NormalizeCostCenter(row.CostCenter));

            var current = buckets.TryGetValue(key, out var existing)
                ? existing
                : (0L, 0L, 0m, 0);

            buckets[key] = (
                current.Item1 + row.PromptTokens,
                current.Item2 + row.CompletionTokens,
                current.Item3 + (row.TotalCost ?? 0m),
                current.Item4 + 1);
        }

        return buckets
            .Select(pair => new DailyUsageRollupRecord(
                pair.Key.UsageDate,
                pair.Key.TenantId,
                pair.Key.ModelId,
                pair.Key.CostCenter,
                pair.Value.Prompt,
                pair.Value.Completion,
                pair.Value.Cost,
                pair.Value.Count))
            .OrderBy(r => r.UsageDate)
            .ThenBy(r => r.TenantId)
            .ThenBy(r => r.ModelId, StringComparer.Ordinal)
            .ThenBy(r => r.CostCenter, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsDuplicateRequestId(DbUpdateException exception)
    {
        // SQLite surfaces a unique-index violation as "SQLite Error 19: 'UNIQUE constraint failed: ...'".
        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("unique", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }
}
