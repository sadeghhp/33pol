using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Persistence.DependencyInjection;
using Pol33.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace Pol33.Persistence.Tests.Integration;

[Trait("Category", "Testcontainers")]
public sealed class PostgresTestcontainersTests
{
    [Fact]
    public async Task Migrations_Apply_AndRepositoryRoundTrip()
    {
        await using var container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .Build();

        await container.StartAsync();

        var services = new ServiceCollection();
        services.AddGatewayPersistence(container.GetConnectionString());
        await using var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        await db.Database.MigrateAsync();

        ITenantRepository tenants = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        IApiKeyRepository keys = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();

        var tenant = await tenants.CreateAsync(new CreateTenantRequest
        {
            Slug = "pg-tenant",
            Name = "Postgres Tenant",
        });

        var key = await keys.CreateAsync(new CreateApiKeyRequest
        {
            TenantId = tenant.Id,
            KeyHash = "pg-hash",
            KeyPrefix = "sk-pg",
            Role = ApiKeyRole.Admin,
        });

        var loaded = await keys.FindByKeyHashAsync("pg-hash");
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(key.Id);
        loaded.Role.Should().Be(ApiKeyRole.Admin);
    }
}
