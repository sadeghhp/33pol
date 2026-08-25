namespace Pol33.Core.Models;

/// <summary>Where a page of errors was served from, so the UI can say so when there is no database.</summary>
public static class GatewayErrorSources
{
    public const string Database = "database";
    public const string Memory = "memory";
}

/// <summary>A page of individual error occurrences.</summary>
public sealed record GatewayErrorPage
{
    public IReadOnlyList<GatewayErrorRecord> Items { get; init; } = [];

    /// <summary>
    /// Rows matching the filter <em>before</em> <see cref="Limit"/> is applied. The Logs tab's
    /// truncation hint is wrong today precisely because its equivalent field is the page length.
    /// </summary>
    public long Total { get; init; }

    public int Limit { get; init; }

    public int Offset { get; init; }

    public string Source { get; init; } = GatewayErrorSources.Memory;

    /// <summary>True when a database-backed store served this page from its in-memory buffer.</summary>
    public bool Degraded { get; init; }
}

/// <summary>A page of grouped errors.</summary>
public sealed record GatewayErrorGroupPage
{
    public IReadOnlyList<GatewayErrorGroup> Items { get; init; } = [];

    /// <summary>Distinct groups matching the filter, before <see cref="Limit"/>.</summary>
    public long Total { get; init; }

    /// <summary>Total occurrences across all matching groups, for the "N occurrences in M groups" header.</summary>
    public long OccurrenceTotal { get; init; }

    /// <summary>
    /// Occurrences held by the store with <em>no</em> filter and <em>no</em> time window applied.
    /// </summary>
    /// <remarks>
    /// The whole point is the empty grid. An operator looking at "3 errors" in the topbar and no rows
    /// cannot tell whether the filter is hiding them, the window is too narrow, or nothing was ever
    /// captured — and those need three different responses. With this number the console states which
    /// it is instead of listing possibilities.
    /// </remarks>
    public long StoredTotal { get; init; }

    /// <summary>Records dropped before persistence because the write buffer was full.</summary>
    public long DroppedTotal { get; init; }

    /// <summary>Records that failed to persist after a retry.</summary>
    public long PersistFailedTotal { get; init; }

    /// <summary>Records deleted by retention since the last clear.</summary>
    public long PrunedTotal { get; init; }

    /// <summary>Age cutoff of the last retention pass; nothing older is stored.</summary>
    public DateTimeOffset? RetainedSinceUtc { get; init; }

    /// <summary>
    /// True when a database-backed store could not reach the archive and served the in-memory
    /// buffer instead. Counts on such a page are lifetime aggregates, not the stored rows.
    /// </summary>
    public bool Degraded { get; init; }

    public int Limit { get; init; }

    public int Offset { get; init; }

    public string Source { get; init; } = GatewayErrorSources.Memory;
}
