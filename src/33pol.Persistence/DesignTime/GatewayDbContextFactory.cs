using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pol33.Persistence.Infrastructure;

namespace Pol33.Persistence.DesignTime;

public sealed class GatewayDbContextFactory : IDesignTimeDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("GATEWAY_DB_CONNECTION")
            ?? "Data Source=gateway.design.db";

        var options = new DbContextOptionsBuilder<GatewayDbContext>();
        SqliteGatewayDbContext.Configure(options, connectionString);

        return new GatewayDbContext(options.Options);
    }
}
