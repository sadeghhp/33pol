using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pol33.Persistence.DesignTime;

public sealed class GatewayDbContextFactory : IDesignTimeDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("GATEWAY_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=33pol_gateway;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(GatewayDbContext).Assembly.GetName().Name))
            .Options;

        return new GatewayDbContext(options);
    }
}
