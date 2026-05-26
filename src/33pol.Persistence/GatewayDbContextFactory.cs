using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pol33.Persistence;

public sealed class GatewayDbContextFactory : IDesignTimeDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__GatewayDb")
            ?? "Host=localhost;Port=5432;Database=33pol;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<GatewayDbContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsAssembly(typeof(GatewayDbContext).Assembly.FullName));
        return new GatewayDbContext(optionsBuilder.Options);
    }
}
