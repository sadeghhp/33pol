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

        lock (_sync)
        {
            var newest = _entries.Last?.Value;
            if (newest is not null &&
                IsSameEvent(newest, stamped) &&
                stamped.TimestampUtc - newest.LastTimestampUtc <= CoalesceWindow)
            {
                newest.Repeats++;
                newest.LastTimestampUtc = stamped.TimestampUtc;
                return;
            }

            _entries.AddLast(stamped);
            while (_entries.Count > _capacity)
            {
                _entries.RemoveFirst();
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

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
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

    private static bool IsSameEvent(GatewayLogEntry a, GatewayLogEntry b) =>
        string.Equals(a.Level, b.Level, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Category, b.Category, StringComparison.Ordinal) &&
        string.Equals(a.EventCode, b.EventCode, StringComparison.Ordinal) &&
        string.Equals(a.Message, b.Message, StringComparison.Ordinal) &&
        string.Equals(a.ModelId, b.ModelId, StringComparison.OrdinalIgnoreCase);

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
