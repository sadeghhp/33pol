using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Diagnostics;
using Pol33.Core.Models;

namespace Pol33.Observability.Diagnostics;

/// <summary>
/// Bounded in-memory error store. Serves the Errors tab on its own when no database is configured,
/// and acts as the hot buffer and aggregate index in front of one when there is.
/// </summary>
/// <remarks>
/// Two structures, for two different jobs. The ring holds recent occurrences with their full
/// detail and is capped so an error storm cannot exhaust memory. The aggregate index holds running
/// totals per fingerprint and <em>outlives</em> the ring — without it, a fault that fired 50,000
/// times would report "1 occurrence, first seen 4 seconds ago" the moment its older rows were
/// evicted, which is worse than useless during an incident.
/// </remarks>
public sealed class InMemoryGatewayErrorStore : IGatewayErrorStore
{
    /// <summary>Window within which the same failure on the same request is treated as one occurrence.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly LinkedList<GatewayErrorRecord> _records = new();
    private readonly Dictionary<string, Aggregate> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _recentlySeen = new(StringComparer.Ordinal);

    // Insertion-ordered shadow of _recentlySeen so eviction is O(evicted) instead of a full scan.
    // Entries whose timestamp no longer matches the dictionary (key re-inserted later) are skipped.
    private readonly Queue<(string Key, DateTimeOffset SeenAt)> _recentlySeenOrder = new();
    private readonly GatewayErrorTrackingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IGatewayErrorArchiveWriter? _archiveWriter;
    private long _sequence;

    public InMemoryGatewayErrorStore(
        IOptions<GatewayErrorTrackingOptions> options,
        TimeProvider timeProvider,
        IGatewayErrorArchiveWriter? archiveWriter = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options.Value;
        _timeProvider = timeProvider;
        _archiveWriter = archiveWriter;
    }

    public bool IsPersistent => false;

    public int Capacity => Math.Max(1, _options.HotBufferCapacity);

    /// <summary>Size of the per-request dedupe map. Exposed for tests.</summary>
    public int RecentlySeenCount
    {
        get
        {
            lock (_sync)
            {
                return _recentlySeen.Count;
            }
        }
    }

    public void Record(GatewayErrorRecord record)
    {
        if (record is null || !_options.Enabled)
        {
            return;
        }

        try
        {
            var normalized = Normalize(record);

            lock (_sync)
            {
                if (IsDuplicateLocked(normalized))
                {
                    return;
                }

                _records.AddLast(normalized);
                while (_records.Count > Capacity)
                {
                    _records.RemoveFirst();
                }

                TrackGroupLocked(normalized);
            }

            _archiveWriter?.Enqueue(normalized);
        }
        catch
        {
            // Recording a failure must never compound it. The request that produced this error is
            // still mid-flight and losing one diagnostic is strictly better than failing it.
        }
    }

    public Task<GatewayErrorPage> QueryAsync(GatewayErrorQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var clamped = query.Clamp(GatewayErrorQuery.MaxExportLimit);

        lock (_sync)
        {
            var matched = _records
                .Where(r => Matches(r, clamped))
                .Reverse()
                .ToList();

            var page = matched
                .Skip(clamped.Offset)
                .Take(clamped.Limit)
                .ToList();

            return Task.FromResult(new GatewayErrorPage
            {
                Items = page,
                Total = matched.Count,
                Limit = clamped.Limit,
                Offset = clamped.Offset,
                Source = GatewayErrorSources.Memory,
            });
        }
    }

    public Task<GatewayErrorGroupPage> QueryGroupsAsync(
        GatewayErrorQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var clamped = query.Clamp();

        lock (_sync)
        {
            // Windowed queries group the occurrences that actually fall in the window, exactly as
            // the database does, so "last hour" never shows a fortnight's count. Only the unbounded
            // view uses the running aggregates, whose whole point is to outlive ring eviction.
            var windowed = clamped.From is not null || clamped.To is not null;
            List<Aggregate> matched = windowed
                ? _records
                    .Where(r => Matches(r, clamped))
                    .GroupBy(r => r.Fingerprint, StringComparer.Ordinal)
                    .Select(g => Aggregate.FromOccurrences(g))
                    .ToList()
                : _groups.Values
                    // Filter on the aggregate's sample: every field the filters touch is part of
                    // the fingerprint, so the sample is representative of the whole group.
                    .Where(a => MatchesGroup(a, clamped))
                    .ToList();

            // Fingerprint tiebreak keeps paging stable when two groups share a timestamp.
            var sorted = clamped.Sort switch
            {
                GatewayErrorSort.Count => matched
                    .OrderByDescending(a => a.Count)
                    .ThenByDescending(a => a.LastSeen)
                    .ThenBy(a => a.Sample.Fingerprint, StringComparer.Ordinal),
                GatewayErrorSort.FirstSeen => matched
                    .OrderByDescending(a => a.FirstSeen)
                    .ThenBy(a => a.Sample.Fingerprint, StringComparer.Ordinal),
                _ => matched
                    .OrderByDescending(a => a.LastSeen)
                    .ThenBy(a => a.Sample.Fingerprint, StringComparer.Ordinal),
            };

            var page = sorted
                .Skip(clamped.Offset)
                .Take(clamped.Limit)
                .Select(a => a.ToGroup())
                .ToList();

            return Task.FromResult(new GatewayErrorGroupPage
            {
                Items = page,
                Total = matched.Count,
                OccurrenceTotal = matched.Sum(a => a.Count),
                StoredTotal = _groups.Values.Sum(a => a.Count),
                Limit = clamped.Limit,
                Offset = clamped.Offset,
                Source = GatewayErrorSources.Memory,
            });
        }
    }

    public Task<GatewayErrorRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult<GatewayErrorRecord?>(null);
        }

        lock (_sync)
        {
            var found = _records.LastOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            return Task.FromResult(found);
        }
    }

    public Task<GatewayErrorFacets> GetFacetsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var window = _records
                .Where(r => (from is null || r.OccurredAt >= from) && (to is null || r.OccurredAt <= to))
                .ToList();

            return Task.FromResult(new GatewayErrorFacets
            {
                Models = Facet(window.Select(r => r.ModelId)),
                Codes = Facet(window.Select(r => r.EventCode)),
                Statuses = Facet(window.Select(r => r.StatusCode == 0 ? null : r.StatusCode.ToString())),
                Levels = Facet(window.Select(r => r.Level)),
            });
        }
    }

    public Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var removed = _records.Count;
            _records.Clear();
            _groups.Clear();
            _recentlySeen.Clear();
            _recentlySeenOrder.Clear();
            return Task.FromResult(removed);
        }
    }

    private GatewayErrorRecord Normalize(GatewayErrorRecord record)
    {
        var occurredAt = record.OccurredAt == default ? _timeProvider.GetUtcNow() : record.OccurredAt;

        // Caps match the archive's declared column widths. SQLite does not enforce them, but a
        // length-enforcing provider rejects the whole batch for one over-long value, and the
        // scrubber's ellipsis counts toward the width.
        var normalized = record with
        {
            Id = string.IsNullOrWhiteSpace(record.Id) ? $"err_{Guid.NewGuid():N}" : record.Id,
            OccurredAt = occurredAt,
            Category = Cap(record.Category, 128) ?? record.Category,
            EventCode = Cap(record.EventCode, 64),
            Message = GatewayErrorRedactor.Scrub(record.Message, _options.MaxMessageLength - 1) ?? record.Message,
            ExceptionType = Cap(record.ExceptionType, 256),
            StackTrace = GatewayErrorRedactor.Scrub(record.StackTrace, _options.MaxStackTraceLength - 1),
            Path = Cap(record.Path, 512),
            UpstreamBodySnippet = GatewayErrorRedactor.Scrub(
                record.UpstreamBodySnippet,
                _options.UpstreamBodySnippetBytes - 1),
            UpstreamTarget = Cap(GatewayErrorRedactor.ScrubUrl(record.UpstreamTarget), 512),
            Outcome = Cap(record.Outcome, 48),
            Hint = Cap(record.Hint, 512),
        };

        // Fingerprint last, over the redacted text: a masked secret must not shatter a group the
        // way the raw rotating value would.
        return normalized with { Fingerprint = GatewayErrorFingerprint.Compute(normalized) };
    }

    /// <summary>
    /// Drops a second capture of the same failure on the same request.
    /// </summary>
    /// <remarks>
    /// One upstream fault is seen by up to three capture points: the proxy records it with the
    /// model, upstream and outcome attached; the terminal handler sees the exception; and the log
    /// sink sees the <c>ILogger</c> call that followed. Without suppression the Errors tab would
    /// count a single failure two or three times, in separate groups.
    /// <para>
    /// Two rules, because they answer different questions. An identical fingerprint on the same
    /// request is always a duplicate. A <em>log-sourced</em> record on a request that a richer
    /// capture point already reported is also a duplicate, even though its fingerprint differs —
    /// the log entry is the same failure seen from further away, and the detailed record is the one
    /// worth keeping.
    /// </para>
    /// </remarks>
    private bool IsDuplicateLocked(GatewayErrorRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.RequestId))
        {
            // Background and pre-request-id failures have nothing to correlate on. Counting them
            // twice is a smaller harm than silently merging two genuinely distinct faults.
            return false;
        }

        var now = record.OccurredAt;
        var isLogSourced = string.Equals(record.Source, GatewayErrorSourceNames.Log, StringComparison.Ordinal);
        var richKey = $"{record.RequestId}|*";

        if (isLogSourced && _recentlySeen.TryGetValue(richKey, out var richSeenAt) && now - richSeenAt <= DuplicateWindow)
        {
            return true;
        }

        var key = $"{record.RequestId}|{record.Fingerprint}";
        if (_recentlySeen.TryGetValue(key, out var seenAt) && now - seenAt <= DuplicateWindow)
        {
            return true;
        }

        RememberLocked(key, now);
        if (!isLogSourced)
        {
            RememberLocked(richKey, now);
        }

        PruneRecentlySeenLocked(now);
        return false;
    }

    private static string? Cap(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    /// <summary>Hard cap on the dedupe map: two keys per record, sized to the hot ring.</summary>
    private int MaxRecentlySeen => Capacity * 2;

    private void RememberLocked(string key, DateTimeOffset now)
    {
        _recentlySeen[key] = now;
        _recentlySeenOrder.Enqueue((key, now));
    }

    private void PruneRecentlySeenLocked(DateTimeOffset now)
    {
        // Drop entries that fell out of the window from the front of the insertion order; stop at
        // the first live one. Under a burst of distinct errors nothing here is stale, so this exits
        // immediately instead of scanning the whole map on every record.
        while (_recentlySeenOrder.TryPeek(out var oldest) && now - oldest.SeenAt > DuplicateWindow)
        {
            _recentlySeenOrder.Dequeue();
            RemoveIfCurrentLocked(oldest);
        }

        // Hard cap: evict oldest by insertion order regardless of age. Dedupe degrades gracefully
        // (a repeat may be counted twice) but memory stays proportional to Capacity, not error rate.
        var cap = MaxRecentlySeen;
        while (_recentlySeen.Count > cap && _recentlySeenOrder.TryDequeue(out var evicted))
        {
            RemoveIfCurrentLocked(evicted);
        }

        // Re-inserted keys leave superseded queue entries behind; keep the queue itself bounded too.
        while (_recentlySeenOrder.Count > cap * 2 && _recentlySeenOrder.TryDequeue(out var superseded))
        {
            RemoveIfCurrentLocked(superseded);
        }
    }

    private void RemoveIfCurrentLocked((string Key, DateTimeOffset SeenAt) entry)
    {
        if (_recentlySeen.TryGetValue(entry.Key, out var current) && current == entry.SeenAt)
        {
            _recentlySeen.Remove(entry.Key);
        }
    }

    private void TrackGroupLocked(GatewayErrorRecord record)
    {
        if (_groups.TryGetValue(record.Fingerprint, out var existing))
        {
            existing.Add(record, ++_sequence);
            return;
        }

        if (_groups.Count >= Math.Max(1, _options.MaxTrackedFingerprints))
        {
            // Evict the group nobody has seen for longest, not the one with the fewest hits — a
            // rare fault that is still firing matters more than a common one that stopped.
            var coldest = _groups.MinBy(kvp => kvp.Value.LastTouched);
            if (coldest.Key is not null)
            {
                _groups.Remove(coldest.Key);
            }
        }

        _groups[record.Fingerprint] = Aggregate.From(record, ++_sequence);
    }

    private static bool Matches(GatewayErrorRecord record, GatewayErrorQuery query) =>
        (query.From is null || record.OccurredAt >= query.From) &&
        (query.To is null || record.OccurredAt <= query.To) &&
        (query.MinimumLevel is not { } floor || GatewayLogLevels.Parse(record.Level) >= floor) &&
        // Ordinal, like the database: the same filter must select the same rows from either store.
        (query.ModelId is null || string.Equals(record.ModelId, query.ModelId, StringComparison.Ordinal)) &&
        (query.StatusCode is null || record.StatusCode == query.StatusCode) &&
        (query.EventCode is null || string.Equals(record.EventCode, query.EventCode, StringComparison.Ordinal)) &&
        (query.TenantId is null || string.Equals(record.TenantId, query.TenantId, StringComparison.Ordinal)) &&
        (query.RequestId is null || string.Equals(record.RequestId, query.RequestId, StringComparison.Ordinal)) &&
        (query.Fingerprint is null || string.Equals(record.Fingerprint, query.Fingerprint, StringComparison.Ordinal)) &&
        (query.Search is null || MatchesSearch(record, query.Search));

    private static bool MatchesSearch(GatewayErrorRecord record, string needle) =>
        Contains(record.Message, needle) ||
        Contains(record.ExceptionType, needle) ||
        Contains(record.EventCode, needle) ||
        Contains(record.ModelId, needle) ||
        Contains(record.RequestId, needle) ||
        Contains(record.Path, needle) ||
        Contains(record.Hint, needle) ||
        Contains(record.StackTrace, needle);

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesGroup(Aggregate aggregate, GatewayErrorQuery query)
    {
        // Time filters compare against the group's span rather than a single instant: a group that
        // is still firing should appear in a "last hour" view even though it started yesterday.
        if (query.From is not null && aggregate.LastSeen < query.From)
        {
            return false;
        }

        if (query.To is not null && aggregate.FirstSeen > query.To)
        {
            return false;
        }

        var probe = aggregate.Sample with { OccurredAt = aggregate.LastSeen };
        return Matches(probe, query with { From = null, To = null });
    }

    private static IReadOnlyList<GatewayErrorFacetValue> Facet(IEnumerable<string?> values) =>
        values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GatewayErrorFacetValue(g.Key, g.LongCount()))
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Value, StringComparer.OrdinalIgnoreCase)
            .Take(GatewayErrorFacets.MaxValuesPerFacet)
            .ToList();

    /// <summary>Running totals for one fingerprint. Survives eviction of its individual records.</summary>
    private sealed class Aggregate
    {
        public required GatewayErrorRecord Sample { get; set; }

        public long Count { get; set; }

        public DateTimeOffset FirstSeen { get; set; }

        public DateTimeOffset LastSeen { get; set; }

        public long LastTouched { get; set; }

        public static Aggregate From(GatewayErrorRecord record, long sequence) => new()
        {
            Sample = record,
            Count = 1,
            FirstSeen = record.OccurredAt,
            LastSeen = record.OccurredAt,
            LastTouched = sequence,
        };

        /// <summary>A window-scoped group built from the occurrences that fell inside it.</summary>
        public static Aggregate FromOccurrences(IEnumerable<GatewayErrorRecord> occurrences)
        {
            Aggregate? aggregate = null;
            foreach (var record in occurrences)
            {
                if (aggregate is null)
                {
                    aggregate = From(record, 0);
                }
                else
                {
                    aggregate.Add(record, 0);
                }
            }

            return aggregate!;
        }

        public void Add(GatewayErrorRecord record, long sequence)
        {
            Count++;
            LastTouched = sequence;

            if (record.OccurredAt < FirstSeen)
            {
                FirstSeen = record.OccurredAt;
            }

            if (record.OccurredAt >= LastSeen)
            {
                LastSeen = record.OccurredAt;
                // Keep the newest occurrence as the sample so the detail panel shows the most
                // recent stack trace and request id rather than a stale first sighting.
                Sample = record;
            }
        }

        public GatewayErrorGroup ToGroup() => new()
        {
            Fingerprint = Sample.Fingerprint,
            Count = Count,
            FirstSeen = FirstSeen,
            LastSeen = LastSeen,
            Level = Sample.Level,
            Message = Sample.Message,
            ExceptionType = Sample.ExceptionType,
            EventCode = Sample.EventCode,
            StatusCode = Sample.StatusCode,
            ModelId = Sample.ModelId,
            Method = Sample.Method,
            Path = Sample.Path,
            UpstreamTarget = Sample.UpstreamTarget,
            Hint = Sample.Hint,
            LastRequestId = Sample.RequestId,
            Sample = Sample,
        };
    }
}
