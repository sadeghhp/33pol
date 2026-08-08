using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class BillingEventRepository(GatewayDbContext dbContext) : IBillingEventRepository
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

    private static bool IsDuplicateRequestId(DbUpdateException exception)
    {
        // SQLite surfaces a unique-index violation as "SQLite Error 19: 'UNIQUE constraint failed: ...'".
        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("unique", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }
}
