namespace Pol33.Core.Abstractions;

/// <summary>Reads the admin audit trail back, newest first, for the Overview's activity card.</summary>
public interface IAuditLogReader
{
    /// <summary>True when a trail exists to read (the file has been created).</summary>
    bool IsAvailable { get; }

    Task<AuditLogReadResult> ReadRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// The newest records matching <paramref name="query"/>. Bounded twice: by how many are returned
    /// and by how many are read to find them, so a filter that matches nothing cannot turn one
    /// request into a scan of the whole trail.
    /// </summary>
    Task<AuditLogReadResult> ReadRecentAsync(AuditLogQuery query, CancellationToken cancellationToken = default);
}

/// <param name="Limit">Most records to return.</param>
/// <param name="ActionPrefix">Only actions that start with this (ordinal); null for all.</param>
/// <param name="BeforeUtc">Only records strictly older than this; null for the newest.</param>
/// <param name="MaxScanned">Most records to read while looking.</param>
public sealed record AuditLogQuery(
    int Limit,
    string? ActionPrefix = null,
    DateTimeOffset? BeforeUtc = null,
    int MaxScanned = 20_000);

public sealed record AuditLogReadResult(IReadOnlyList<AuditLogEntryView> Entries, int ParseErrors, DateTimeOffset? NewestUtc)
{
    /// <summary>Whether at least one older matching record exists beyond the ones returned.</summary>
    public bool HasMore { get; init; }

    /// <summary>
    /// Whether reading stopped at the scan ceiling rather than at the end of the trail, so older
    /// matches may exist that this call did not reach.
    /// </summary>
    public bool ScanLimitReached { get; init; }
}

/// <param name="Details">The action's details re-serialised as compact JSON; null when the record had none.</param>
public sealed record AuditLogEntryView(
    DateTimeOffset TimestampUtc,
    string Action,
    string? TenantId,
    string? ApiKeyId,
    string? Details);
