using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Observability.Diagnostics;

/// <summary>
/// Fixed-capacity ring of recent diagnostics. In-process and non-durable by design: this is the
/// "what just went wrong" pane, not the audit trail — durable logging stays with the configured
/// <c>ILogger</c> providers, which this store mirrors rather than replaces.
/// </summary>
public sealed class InMemoryGatewayLogStore : IGatewayLogStore
{
    /// <summary>Window within which an identical event folds into the previous entry as a repeat.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(30);

    public const int DefaultCapacity = 500;

    public const int MaxDetailLength = 4000;

    private readonly object _sync = new();
    private readonly LinkedList<GatewayLogEntry> _entries = new();

    /// <summary>
    /// Identity → node, so a repeat folds into its own earlier entry rather than only into the
    /// newest one. With tail-only matching, two upstreams failing alternately never coalesced and
    /// between them evicted every other diagnostic — the exact outcome coalescing exists to prevent.
    /// </summary>
    private readonly Dictionary<string, LinkedListNode<GatewayLogEntry>> _byIdentity = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly TimeProvider _timeProvider;

    public InMemoryGatewayLogStore()
        : this(DefaultCapacity, TimeProvider.System)
    {
    }

    public InMemoryGatewayLogStore(int capacity, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _capacity = capacity;
        _timeProvider = timeProvider;
    }

    public int Capacity => _capacity;

    public void Record(GatewayLogEntry entry)
    {
        if (entry is null)
        {
            return;
        }

        var stamped = Normalize(entry);

        var identity = IdentityOf(stamped);

        lock (_sync)
        {
            if (_byIdentity.TryGetValue(identity, out var node) &&
                stamped.TimestampUtc - node.Value.LastTimestampUtc <= CoalesceWindow)
            {
                // Replace rather than mutate: readers hold references to these entries and
                // serialize them outside the lock.
                node.Value = node.Value with
                {
                    Repeats = node.Value.Repeats + 1,
                    LastTimestampUtc = stamped.TimestampUtc,
                };
                return;
            }

            if (node is not null)
            {
                // Outside the window — the old entry stays as history and this identity now points
                // at the new one.
                _byIdentity.Remove(identity);
            }

            var added = _entries.AddLast(stamped);
            _byIdentity[identity] = added;

            while (_entries.Count > _capacity)
            {
                var evicted = _entries.First!;
                _entries.RemoveFirst();

                var evictedIdentity = IdentityOf(evicted.Value);
                if (_byIdentity.TryGetValue(evictedIdentity, out var tracked) && tracked == evicted)
                {
                    _byIdentity.Remove(evictedIdentity);
                }
            }
        }
    }

    public IReadOnlyList<GatewayLogEntry> GetRecent(
        int limit,
        GatewayLogLevel? minimumLevel = null,
        string? search = null)
    {
        var take = Math.Clamp(limit, 1, _capacity);
        var needle = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        lock (_sync)
        {
            var results = new List<GatewayLogEntry>(Math.Min(take, _entries.Count));
            for (var node = _entries.Last; node is not null && results.Count < take; node = node.Previous)
            {
                var entry = node.Value;
                if (minimumLevel is { } floor && GatewayLogLevels.Parse(entry.Level) < floor)
                {
                    continue;
                }

                if (needle is not null && !Matches(entry, needle))
                {
                    continue;
                }

                results.Add(entry);
            }

            return results;
        }
    }

    public int Clear()
    {
        lock (_sync)
        {
            var removed = _entries.Count;
            _entries.Clear();
            _byIdentity.Clear();
            return removed;
        }
    }

    private GatewayLogEntry Normalize(GatewayLogEntry entry)
    {
        var timestamp = entry.TimestampUtc == default
            ? _timeProvider.GetUtcNow()
            : entry.TimestampUtc;

        return new GatewayLogEntry
        {
            Id = string.IsNullOrWhiteSpace(entry.Id) ? $"log_{Guid.NewGuid():N}" : entry.Id,
            TimestampUtc = timestamp,
            Level = entry.Level,
            Category = entry.Category,
            EventCode = entry.EventCode,
            Message = entry.Message,
            Detail = Truncate(entry.Detail),
            Hint = entry.Hint,
            ModelId = entry.ModelId,
            RequestId = entry.RequestId,
            Repeats = entry.Repeats < 1 ? 1 : entry.Repeats,
            LastTimestampUtc = timestamp,
        };
    }

    /// <summary>The five fields that make two entries "the same event" for coalescing purposes.</summary>
    private static string IdentityOf(GatewayLogEntry entry) => string.Join(
        '\u001f',
        entry.Level.ToLowerInvariant(),
        entry.Category,
        entry.EventCode ?? string.Empty,
        entry.Message,
        entry.ModelId?.ToLowerInvariant() ?? string.Empty);

    private static bool Matches(GatewayLogEntry entry, string needle) =>
        Contains(entry.Message, needle) ||
        Contains(entry.Category, needle) ||
        Contains(entry.EventCode, needle) ||
        Contains(entry.ModelId, needle) ||
        Contains(entry.RequestId, needle) ||
        Contains(entry.Hint, needle) ||
        Contains(entry.Detail, needle);

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string? Truncate(string? detail) =>
        detail is null || detail.Length <= MaxDetailLength
            ? detail
            : detail[..MaxDetailLength] + "…";
}
