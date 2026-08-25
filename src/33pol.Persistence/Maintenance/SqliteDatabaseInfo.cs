using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;

namespace Pol33.Persistence.Maintenance;

/// <summary>Reads the live database file's location and size; mirrors <see cref="SqliteBackupService"/>'s path rules.</summary>
public sealed class SqliteDatabaseInfo(GatewayDbContext dbContext) : ISqliteDatabaseInfo
{
    private string? Resolve()
    {
        if (!dbContext.Database.IsRelational() || dbContext.Database.GetDbConnection() is not SqliteConnection connection)
        {
            return null;
        }

        var dataSource = connection.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(dataSource);
    }

    public bool IsSqliteFile => Resolve() is not null;

    public string? DatabasePath => Resolve();

    public string? BackupDirectory => Resolve() is { } path ? Path.Combine(Path.GetDirectoryName(path)!, "backups") : null;

    public long? SizeBytes
    {
        get
        {
            if (Resolve() is not { } path)
            {
                return null;
            }

            long total = 0;
            foreach (var candidate in new[] { path, path + "-wal" })
            {
                var info = new FileInfo(candidate);
                if (info.Exists)
                {
                    total += info.Length;
                }
            }

            return total;
        }
    }

    public async Task<string?> GetJournalModeAsync(CancellationToken cancellationToken = default)
    {
        if (Resolve() is null)
        {
            return null;
        }

        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result?.ToString();
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
