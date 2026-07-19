using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;

namespace Pol33.Persistence.Maintenance;

/// <summary>
/// Hot backup via <c>VACUUM INTO</c>: produces a transactionally-consistent single-file copy of the
/// live database with no gateway downtime (safe under WAL while the gateway keeps serving), then runs
/// <c>PRAGMA integrity_check</c> on the copy so a corrupt snapshot is caught immediately rather than at
/// restore time. Backups are written to a <c>backups/</c> directory beside the live database file, which
/// the deploy tooling then copies off the volume.
/// </summary>
public sealed class SqliteBackupService : ISqliteBackupService
{
    private readonly GatewayDbContext _dbContext;
    private readonly ILogger<SqliteBackupService> _logger;

    public SqliteBackupService(GatewayDbContext dbContext, ILogger<SqliteBackupService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<SqliteBackupResult> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.GetDbConnection() is not SqliteConnection connection)
        {
            return SqliteBackupResult.NotConfigured();
        }

        var dataSource = connection.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return SqliteBackupResult.NotConfigured();
        }

        var backupDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataSource))!, "backups");
        Directory.CreateDirectory(backupDir);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var destination = Path.Combine(backupDir, $"gateway-{timestamp}.db");

        // Server-generated path, but quote-escape defensively; VACUUM INTO takes a string literal, not a parameter.
        var literal = destination.Replace("'", "''");

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = $"VACUUM INTO '{literal}'";
            await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var integrity = await CheckIntegrityAsync(destination, cancellationToken).ConfigureAwait(false);
        var size = new FileInfo(destination).Length;
        var ok = string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase);

        if (ok)
        {
            _logger.LogInformation("Created SQLite hot backup {Path} ({Size} bytes), integrity_check ok.", destination, size);
        }
        else
        {
            _logger.LogError("SQLite hot backup {Path} failed integrity_check: {Result}", destination, integrity);
        }

        return new SqliteBackupResult(
            ok,
            destination,
            size,
            integrity,
            ok ? null : $"integrity_check returned '{integrity}'");
    }

    private static async Task<string> CheckIntegrityAsync(string path, CancellationToken cancellationToken)
    {
        await using var checkConnection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await checkConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = checkConnection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result?.ToString() ?? "unknown";
    }
}

/// <summary>Fallback when no SQLite database is configured (in-memory tests, DB-less deployments).</summary>
public sealed class NullSqliteBackupService : ISqliteBackupService
{
    public Task<SqliteBackupResult> CreateBackupAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SqliteBackupResult.NotConfigured());
}
