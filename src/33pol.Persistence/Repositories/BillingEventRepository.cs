using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
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

    private static bool IsDuplicateRequestId(DbUpdateException exception)
    {
        if (exception.InnerException is PostgresException postgres)
        {
            return postgres.SqlState == "23505";
        }

        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("unique", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }
}
