namespace Pol33.Core.Models;

/// <summary>How far a clear-all-errors reached. Reported back to the operator and to the audit log.</summary>
public sealed record GatewayErrorClearResult
{
    /// <summary>Error records removed from the buffer and, when configured, the database.</summary>
    public int RecordsDeleted { get; init; }

    /// <summary>
    /// False when the stored records could not be deleted (the database was unreachable). The
    /// counters were still reset, so the two views disagree until the operator retries.
    /// </summary>
    public bool ArchiveCleared { get; init; } = true;

    /// <summary>Failed rows dropped from the Recent requests feed.</summary>
    public int RecentRequestRowsRemoved { get; init; }

    /// <summary>The value <c>totalErrors</c> held before it was zeroed.</summary>
    public long TotalErrorsCleared { get; init; }

    /// <summary>
    /// True when the persisted counter snapshot was rewritten. False means a restart would have
    /// restored the old totals — which is the whole reason this flag is reported.
    /// </summary>
    public bool SnapshotRewritten { get; init; }

    public bool DatabaseAvailable { get; init; }
}
