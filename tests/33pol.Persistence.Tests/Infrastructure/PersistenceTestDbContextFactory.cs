using Microsoft.EntityFrameworkCore;
using Pol33.Persistence;

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

    public static GatewayDbContext CreateNpgsql(string connectionString)
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(GatewayDbContext).Assembly.GetName().Name))
            .Options;

        return new GatewayDbContext(options);
    }
}
