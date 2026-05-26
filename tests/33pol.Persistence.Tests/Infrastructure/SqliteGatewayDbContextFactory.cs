using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pol33.Persistence;

namespace Pol33.Persistence.Tests.Infrastructure;

internal static class SqliteGatewayDbContextFactory
{
    public static async Task<GatewayDbContext> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new GatewayDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
