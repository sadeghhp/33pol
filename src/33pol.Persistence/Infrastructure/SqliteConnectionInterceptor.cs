using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Pol33.Persistence.Infrastructure;

/// <summary>
/// Applies the per-connection SQLite pragmas the gateway relies on. SQLite defaults
/// foreign-key enforcement OFF and every pragma below is connection-scoped, so they must be
/// re-applied every time EF opens a connection (connection pooling reuses handles).
/// </summary>
internal sealed class SqliteConnectionInterceptor : DbConnectionInterceptor
{
    // foreign_keys: schema uses cascade deletes (tenant -> api keys/grants); SQLite defaults it off.
    // journal_mode=WAL: better read/write concurrency + durability (no-op / harmless on in-memory dbs).
    // busy_timeout: wait out transient SQLITE_BUSY instead of throwing.
    // synchronous=NORMAL: safe with WAL, good write throughput.
    private const string Pragmas =
        "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
        => await ApplyAsync(connection, cancellationToken).ConfigureAwait(false);

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
