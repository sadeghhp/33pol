namespace Pol33.Core.Abstractions;

/// <summary>
/// Produces a consistent, online (no-downtime) copy of the embedded SQLite database using
/// <c>VACUUM INTO</c>, which snapshots a transactionally-consistent image even while the gateway is
/// writing under WAL. Returns a degraded result (not an exception) when no relational database is
/// configured, so the admin surface can report cleanly.
/// </summary>
public interface ISqliteBackupService
{
    Task<SqliteBackupResult> CreateBackupAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a hot backup attempt.</summary>
public sealed record SqliteBackupResult(
    bool Succeeded,
    string? Path,
    long SizeBytes,
    string IntegrityCheck,
    string? Error)
{
    public static SqliteBackupResult NotConfigured() =>
        new(false, null, 0, "skipped", "No relational database is configured; nothing to back up.");
}
