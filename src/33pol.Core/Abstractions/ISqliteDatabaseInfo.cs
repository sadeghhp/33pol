namespace Pol33.Core.Abstractions;

/// <summary>Facts about the embedded database file, for the admin Overview.</summary>
public interface ISqliteDatabaseInfo
{
    /// <summary>True when a SQLite file backs the gateway (false for the in-memory test provider or no database).</summary>
    bool IsSqliteFile { get; }

    string? DatabasePath { get; }

    /// <summary>Directory hot backups are written to (<c>backups/</c> beside the live file); null when not file-backed.</summary>
    string? BackupDirectory { get; }

    /// <summary>Size of the main file plus its WAL, or null when not file-backed.</summary>
    long? SizeBytes { get; }

    Task<string?> GetJournalModeAsync(CancellationToken cancellationToken = default);
}
