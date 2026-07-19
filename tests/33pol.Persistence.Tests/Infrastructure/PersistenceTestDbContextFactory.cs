using Microsoft.EntityFrameworkCore;
using Pol33.Persistence;
using Pol33.Persistence.Infrastructure;

namespace Pol33.Persistence.Tests.Infrastructure;

internal static class PersistenceTestDbContextFactory
{
    public static GatewayDbContext CreateInMemory(string databaseName)
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new GatewayDbContext(options);
    }

    /// <summary>
    /// Builds a context against a real SQLite database (file or shared in-memory), applying the
    /// same pragmas and interceptors as production so migration/constraint behaviour is exercised.
    /// </summary>
    public static GatewayDbContext CreateSqlite(string connectionString)
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>();
        SqliteGatewayDbContext.Configure(options, connectionString);
        return new GatewayDbContext(options.Options);
    }
}
