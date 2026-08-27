using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Repositories;

/// <summary>
/// Durable storage for gateway errors, backing the admin Errors tab's grouped view, occurrence
/// paging, facets and retention.
/// </summary>
/// <remarks>
/// Deletes go through <c>RemoveRange</c> over a fetched page rather than <c>ExecuteDelete</c>: the
/// EF InMemory provider used across the test suite does not implement <c>ExecuteDelete</c>, so the
/// faster call would make every one of these paths untestable. Pages of
/// <see cref="DeletePageSize"/> keep the memory cost bounded; the trade-off is that a clear is not
/// atomic, which is acceptable for an operator-initiated, idempotent action.
/// </remarks>
public sealed class GatewayErrorRepository(GatewayDbContext dbContext) : IGatewayErrorArchive
{
    private const int DeletePageSize = 5000;

    public async Task AppendBatchAsync(
        IReadOnlyList<GatewayErrorRecord> batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        dbContext.GatewayErrors.AddRange(batch.Select(ToEntity));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GatewayErrorPage> QueryAsync(
        GatewayErrorQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var clamped = query.Clamp(GatewayErrorQuery.MaxExportLimit);
        var filtered = Filter(dbContext.GatewayErrors.AsNoTracking(), clamped);

        var total = await filtered.LongCountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await filtered
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Skip(clamped.Offset)
            .Take(clamped.Limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new GatewayErrorPage
        {
            Items = rows.Select(ToRecord).ToList(),
            Total = total,
            Limit = clamped.Limit,
            Offset = clamped.Offset,
            Source = GatewayErrorSources.Database,
        };
    }

    public async Task<GatewayErrorGroupPage> QueryGroupsAsync(
        GatewayErrorQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var clamped = query.Clamp();
        var filtered = Filter(dbContext.GatewayErrors.AsNoTracking(), clamped);

        var grouped = filtered
            .GroupBy(e => e.Fingerprint)
            .Select(g => new GroupProjection
            {
                Fingerprint = g.Key,
                Count = g.LongCount(),
                FirstSeen = g.Min(e => e.OccurredAt),
                LastSeen = g.Max(e => e.OccurredAt),
                NewestId = g.Max(e => e.Id),
            });

        var total = await grouped.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var occurrenceTotal = await filtered.LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Unfiltered, so an empty grid can distinguish "the window hides them" from "nothing was
        // ever captured". One indexed COUNT over a table retention keeps bounded.
        var storedTotal = await dbContext.GatewayErrors
            .AsNoTracking()
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        // NewestId is unique per group, so it makes every ordering total: groups that share a
        // timestamp to the tick cannot swap places between one page and the next.
        var ordered = clamped.Sort switch
        {
            GatewayErrorSort.Count => grouped
                .OrderByDescending(g => g.Count)
                .ThenByDescending(g => g.LastSeen)
                .ThenByDescending(g => g.NewestId),
            GatewayErrorSort.FirstSeen => grouped
                .OrderByDescending(g => g.FirstSeen)
                .ThenByDescending(g => g.NewestId),
            _ => grouped
                .OrderByDescending(g => g.LastSeen)
                .ThenByDescending(g => g.NewestId),
        };

        var page = await ordered
            .Skip(clamped.Offset)
            .Take(clamped.Limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (page.Count == 0)
        {
            return new GatewayErrorGroupPage
            {
                Total = total,
                OccurrenceTotal = occurrenceTotal,
                StoredTotal = storedTotal,
                Limit = clamped.Limit,
                Offset = clamped.Offset,
                Source = GatewayErrorSources.Database,
            };
        }

        // Two round-trips rather than a correlated subquery: SQLite plans the latter poorly, and
        // the second query is a primary-key lookup over at most one page of ids.
        var sampleIds = page.Select(g => g.NewestId).ToList();
        var samples = await dbContext.GatewayErrors
            .AsNoTracking()
            .Where(e => sampleIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);

        var groups = page
            .Where(g => samples.ContainsKey(g.NewestId))
            .Select(g => ToGroup(g, ToRecord(samples[g.NewestId])))
            .ToList();

        return new GatewayErrorGroupPage
        {
            Items = groups,
            Total = total,
            OccurrenceTotal = occurrenceTotal,
            StoredTotal = storedTotal,
            Limit = clamped.Limit,
            Offset = clamped.Offset,
            Source = GatewayErrorSources.Database,
        };
    }

    public async Task<GatewayErrorRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var entity = await dbContext.GatewayErrors
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.RecordId == id, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<GatewayErrorFacets> GetFacetsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken = default)
    {
        var window = dbContext.GatewayErrors.AsNoTracking();
        if (from is not null)
        {
            window = window.Where(e => e.OccurredAt >= from);
        }

        if (to is not null)
        {
            window = window.Where(e => e.OccurredAt <= to);
        }

        return new GatewayErrorFacets
        {
            Models = await FacetAsync(window.Where(e => e.ModelId != null).Select(e => e.ModelId!), cancellationToken)
                .ConfigureAwait(false),
            Codes = await FacetAsync(window.Where(e => e.EventCode != null).Select(e => e.EventCode!), cancellationToken)
                .ConfigureAwait(false),
            Statuses = await FacetAsync(
                    window.Where(e => e.StatusCode != 0).Select(e => e.StatusCode.ToString()),
                    cancellationToken)
                .ConfigureAwait(false),
            Levels = await FacetAsync(window.Select(e => e.Level), cancellationToken).ConfigureAwait(false),
        };
    }

    public Task<bool> HasEventsForKeyAsync(string apiKeyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKeyId))
        {
            return Task.FromResult(false);
        }

        return dbContext.GatewayErrors
            .AsNoTracking()
            .AnyAsync(e => e.ApiKeyId == apiKeyId, cancellationToken);
    }

    public async Task<IReadOnlySet<string>> FindKeysWithEventsAsync(
        IReadOnlyCollection<string> apiKeyIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiKeyIds);
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (apiKeyIds.Count == 0)
        {
            return found;
        }

        foreach (var chunk in apiKeyIds.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            var present = await dbContext.GatewayErrors
                .AsNoTracking()
                .Where(e => e.ApiKeyId != null && chunk.Contains(e.ApiKeyId))
                .Select(e => e.ApiKeyId!)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            found.UnionWith(present);
        }

        return found;
    }

    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken = default) =>
        await DeleteWhereAsync(dbContext.GatewayErrors, int.MaxValue, cancellationToken).ConfigureAwait(false);

    public async Task<int> PruneAsync(
        DateTimeOffset olderThan,
        int maxRows,
        CancellationToken cancellationToken = default)
    {
        var removed = await DeleteWhereAsync(
                dbContext.GatewayErrors.Where(e => e.OccurredAt < olderThan),
                int.MaxValue,
                cancellationToken)
            .ConfigureAwait(false);

        if (maxRows <= 0)
        {
            return removed;
        }

        var remaining = await dbContext.GatewayErrors.CountAsync(cancellationToken).ConfigureAwait(false);
        if (remaining <= maxRows)
        {
            return removed;
        }

        // Oldest-first, so the trim keeps the most recent window an operator is likely looking at.
        removed += await DeleteWhereAsync(
                dbContext.GatewayErrors.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id),
                remaining - maxRows,
                cancellationToken)
            .ConfigureAwait(false);

        return removed;
    }

    private async Task<int> DeleteWhereAsync(
        IQueryable<GatewayErrorEntity> source,
        int limit,
        CancellationToken cancellationToken)
    {
        var deleted = 0;

        while (deleted < limit)
        {
            var take = Math.Min(DeletePageSize, limit - deleted);
            var page = await source.Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            dbContext.GatewayErrors.RemoveRange(page);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            deleted += page.Count;

            if (page.Count < take)
            {
                break;
            }
        }

        return deleted;
    }

    private static async Task<IReadOnlyList<GatewayErrorFacetValue>> FacetAsync(
        IQueryable<string> values,
        CancellationToken cancellationToken)
    {
        var rows = await values
            .GroupBy(v => v)
            .Select(g => new { Value = g.Key, Count = g.LongCount() })
            .OrderByDescending(x => x.Count)
            .Take(GatewayErrorFacets.MaxValuesPerFacet)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(r => new GatewayErrorFacetValue(r.Value, r.Count)).ToList();
    }

    private static IQueryable<GatewayErrorEntity> Filter(
        IQueryable<GatewayErrorEntity> source,
        GatewayErrorQuery query)
    {
        if (query.From is { } from)
        {
            source = source.Where(e => e.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            source = source.Where(e => e.OccurredAt <= to);
        }

        if (query.MinimumLevel is { } floor)
        {
            // The level is stored as a name, so the floor is expanded into the set of names at or
            // above it. Translating a Parse call into SQL is not possible.
            var allowed = Enum.GetValues<GatewayLogLevel>()
                .Where(l => l >= floor)
                .Select(l => l.ToString())
                .ToList();
            source = source.Where(e => allowed.Contains(e.Level));
        }

        if (query.ModelId is { } modelId)
        {
            source = source.Where(e => e.ModelId == modelId);
        }

        if (query.StatusCode is { } status)
        {
            source = source.Where(e => e.StatusCode == status);
        }

        if (query.EventCode is { } code)
        {
            source = source.Where(e => e.EventCode == code);
        }

        if (query.TenantId is { } tenantId)
        {
            source = source.Where(e => e.TenantId == tenantId);
        }

        if (query.RequestId is { } requestId)
        {
            source = source.Where(e => e.RequestId == requestId);
        }

        if (query.Fingerprint is { } fingerprint)
        {
            source = source.Where(e => e.Fingerprint == fingerprint);
        }

        if (query.Search is { } search)
        {
            // LIKE, unindexed, and case-insensitive only for ASCII on SQLite. It scans the filtered
            // window, which retention keeps bounded.
            // The explicit escape character matters: SQLite LIKE has no bracket classes, so
            // "[_]" would mean literal '[', any char, literal ']' and a search for "req_abc" (every
            // gateway request id contains '_') could never match.
            var pattern = $"%{Escape(search)}%";
            source = source.Where(e =>
                EF.Functions.Like(e.Message, pattern, LikeEscape) ||
                (e.ExceptionType != null && EF.Functions.Like(e.ExceptionType, pattern, LikeEscape)) ||
                (e.EventCode != null && EF.Functions.Like(e.EventCode, pattern, LikeEscape)) ||
                (e.ModelId != null && EF.Functions.Like(e.ModelId, pattern, LikeEscape)) ||
                (e.RequestId != null && EF.Functions.Like(e.RequestId, pattern, LikeEscape)) ||
                (e.Path != null && EF.Functions.Like(e.Path, pattern, LikeEscape)) ||
                (e.Hint != null && EF.Functions.Like(e.Hint, pattern, LikeEscape)) ||
                (e.StackTrace != null && EF.Functions.Like(e.StackTrace, pattern, LikeEscape)));
        }

        return source;
    }

    private const string LikeEscape = "\\";

    /// <summary>
    /// Neutralizes LIKE wildcards (and the escape character itself) with a backslash escape so a
    /// search for "100%" or "req_abc" matches literally instead of acting as a wildcard.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static GatewayErrorGroup ToGroup(GroupProjection projection, GatewayErrorRecord sample) => new()
    {
        Fingerprint = projection.Fingerprint,
        Count = projection.Count,
        FirstSeen = projection.FirstSeen,
        LastSeen = projection.LastSeen,
        Level = sample.Level,
        Message = sample.Message,
        ExceptionType = sample.ExceptionType,
        EventCode = sample.EventCode,
        StatusCode = sample.StatusCode,
        ModelId = sample.ModelId,
        Method = sample.Method,
        Path = sample.Path,
        UpstreamTarget = sample.UpstreamTarget,
        Hint = sample.Hint,
        LastRequestId = sample.RequestId,
        Sample = sample,
    };

    private static GatewayErrorEntity ToEntity(GatewayErrorRecord record) => new()
    {
        RecordId = record.Id,
        Fingerprint = record.Fingerprint,
        OccurredAt = record.OccurredAt,
        Level = record.Level,
        Source = record.Source,
        Category = record.Category,
        EventCode = record.EventCode,
        Message = record.Message,
        ExceptionType = record.ExceptionType,
        StackTrace = record.StackTrace,
        Method = record.Method,
        Path = record.Path,
        RouteKind = record.RouteKind,
        StatusCode = record.StatusCode,
        ModelId = record.ModelId,
        UpstreamTarget = record.UpstreamTarget,
        Outcome = record.Outcome,
        TenantId = record.TenantId,
        ApiKeyId = record.ApiKeyId,
        RequestId = record.RequestId,
        DurationMs = record.DurationMs,
        UpstreamBodySnippet = record.UpstreamBodySnippet,
        Hint = record.Hint,
    };

    private static GatewayErrorRecord ToRecord(GatewayErrorEntity entity) => new()
    {
        Id = entity.RecordId,
        Fingerprint = entity.Fingerprint,
        OccurredAt = entity.OccurredAt,
        Level = entity.Level,
        Source = entity.Source,
        Category = entity.Category,
        EventCode = entity.EventCode,
        Message = entity.Message,
        ExceptionType = entity.ExceptionType,
        StackTrace = entity.StackTrace,
        Method = entity.Method,
        Path = entity.Path,
        RouteKind = entity.RouteKind,
        StatusCode = entity.StatusCode,
        ModelId = entity.ModelId,
        UpstreamTarget = entity.UpstreamTarget,
        Outcome = entity.Outcome,
        TenantId = entity.TenantId,
        ApiKeyId = entity.ApiKeyId,
        RequestId = entity.RequestId,
        DurationMs = entity.DurationMs,
        UpstreamBodySnippet = entity.UpstreamBodySnippet,
        Hint = entity.Hint,
    };

    private sealed class GroupProjection
    {
        public required string Fingerprint { get; init; }

        public long Count { get; init; }

        public DateTimeOffset FirstSeen { get; init; }

        public DateTimeOffset LastSeen { get; init; }

        public long NewestId { get; init; }
    }
}
