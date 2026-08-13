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
            // Filter on the aggregate's sample: every field the filters touch is part of the
            // fingerprint, so the sample is representative of the whole group by construction.
            var matched = _groups.Values
                .Where(a => MatchesGroup(a, clamped))
                .ToList();

            var sorted = clamped.Sort switch
            {
                GatewayErrorSort.Count => matched.OrderByDescending(a => a.Count).ThenByDescending(a => a.LastSeen),
                GatewayErrorSort.FirstSeen => matched.OrderByDescending(a => a.FirstSeen),
                _ => matched.OrderByDescending(a => a.LastSeen),
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
            return Task.FromResult(removed);
        }
    }

    private GatewayErrorRecord Normalize(GatewayErrorRecord record)
    {
        var occurredAt = record.OccurredAt == default ? _timeProvider.GetUtcNow() : record.OccurredAt;

        var normalized = record with
        {
            Id = string.IsNullOrWhiteSpace(record.Id) ? $"err_{Guid.NewGuid():N}" : record.Id,
            OccurredAt = occurredAt,
            Message = GatewayErrorRedactor.Scrub(record.Message, _options.MaxMessageLength) ?? record.Message,
            StackTrace = GatewayErrorRedactor.Scrub(record.StackTrace, _options.MaxStackTraceLength),
            UpstreamBodySnippet = GatewayErrorRedactor.Scrub(
                record.UpstreamBodySnippet,
                _options.UpstreamBodySnippetBytes),
            UpstreamTarget = GatewayErrorRedactor.ScrubUrl(record.UpstreamTarget),
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

        _recentlySeen[key] = now;
        if (!isLogSourced)
        {
            _recentlySeen[richKey] = now;
        }

        if (_recentlySeen.Count > Capacity * 2)
        {
            var stale = _recentlySeen
                .Where(kvp => now - kvp.Value > DuplicateWindow)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var expired in stale)
            {
                _recentlySeen.Remove(expired);
            }
        }

        return false;
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
        (query.ModelId is null || string.Equals(record.ModelId, query.ModelId, StringComparison.OrdinalIgnoreCase)) &&
        (query.StatusCode is null || record.StatusCode == query.StatusCode) &&
        (query.EventCode is null || string.Equals(record.EventCode, query.EventCode, StringComparison.OrdinalIgnoreCase)) &&
        (query.TenantId is null || string.Equals(record.TenantId, query.TenantId, StringComparison.OrdinalIgnoreCase)) &&
        (query.RequestId is null || string.Equals(record.RequestId, query.RequestId, StringComparison.OrdinalIgnoreCase)) &&
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
