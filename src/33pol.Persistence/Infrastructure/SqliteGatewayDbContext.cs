using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Pol33.Persistence.Infrastructure;

/// <summary>
/// Single source of truth for configuring <see cref="GatewayDbContext"/> against SQLite —
/// used by the runtime DI wiring, the design-time factory, and tests so the pragmas and
/// data-directory handling stay identical everywhere.
/// </summary>
public static class SqliteGatewayDbContext
{
    public static void Configure(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        EnsureDataDirectory(connectionString);

        options.UseSqlite(
                connectionString,
                sqlite => sqlite.MigrationsAssembly(typeof(GatewayDbContext).Assembly.GetName().Name))
            .AddInterceptors(new SqliteConnectionInterceptor());
    }

    /// <summary>
    /// SQLite creates the database file but not its parent directory; create it up front for
    /// file-backed databases so first boot against a fresh volume succeeds.
    /// </summary>
    public static void EnsureDataDirectory(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);

        if (builder.Mode == SqliteOpenMode.Memory)
        {
            return;
        }

        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
