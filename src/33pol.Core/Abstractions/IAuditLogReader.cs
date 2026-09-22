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
/// <param name="Cursor">Where to resume; null for the newest page.</param>
/// <param name="MaxScanned">Most records to read while looking.</param>
public sealed record AuditLogQuery(
    int Limit,
    string? ActionPrefix = null,
    AuditLogCursor? Cursor = null,
    int MaxScanned = 20_000);

/// <summary>
/// Where a page of the trail stopped: a timestamp, plus how many records carrying exactly that
/// timestamp have already been returned.
/// </summary>
/// <remarks>
/// The count is what makes paging safe. The trail is ordered by append, not by a unique key, and
/// nothing stops two records sharing a timestamp — a coarse platform clock, a fixed
/// <see cref="TimeProvider"/> in a test, or two replicas appending to one file all produce them. A
/// cursor that is only a timestamp has to choose between dropping every record in the boundary
/// group and returning it twice; carrying the count resumes exactly where the page ended.
/// </remarks>
/// <param name="TimestampUtc">The last returned record's timestamp.</param>
/// <param name="Skip">How many records with that timestamp were already returned.</param>
public sealed record AuditLogCursor(DateTimeOffset TimestampUtc, int Skip)
{
    /// <summary>Round-trips through <see cref="TryParse"/>; opaque to clients.</summary>
    public override string ToString() =>
        TimestampUtc.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture)
        + (Skip > 0 ? "|" + Skip.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);

    /// <summary>A plain timestamp is accepted too, so a hand-written query still works.</summary>
    public static bool TryParse(string? value, out AuditLogCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var bar = value.IndexOf('|', StringComparison.Ordinal);
        var stamp = bar < 0 ? value : value[..bar];
        var skip = 0;

        if (bar >= 0 && !int.TryParse(
                value[(bar + 1)..],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out skip))
        {
            return false;
        }

        if (skip < 0 || !DateTimeOffset.TryParse(
                stamp,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return false;
        }

        cursor = new AuditLogCursor(parsed.ToUniversalTime(), skip);
        return true;
    }
}

public sealed record AuditLogReadResult(IReadOnlyList<AuditLogEntryView> Entries, int ParseErrors, DateTimeOffset? NewestUtc)
{
    /// <summary>Whether at least one older matching record exists beyond the ones returned.</summary>
    public bool HasMore { get; init; }

    /// <summary>
    /// Whether reading stopped at the scan ceiling rather than at the end of the trail, so older
    /// matches may exist that this call did not reach.
    /// </summary>
    public bool ScanLimitReached { get; init; }

    /// <summary>Where to resume for the next page; null when <see cref="HasMore"/> is false.</summary>
    public AuditLogCursor? NextCursor { get; init; }
}

/// <param name="Details">The action's details re-serialised as compact JSON; null when the record had none.</param>
public sealed record AuditLogEntryView(
    DateTimeOffset TimestampUtc,
    string Action,
    string? TenantId,
    string? ApiKeyId,
    string? Details);
