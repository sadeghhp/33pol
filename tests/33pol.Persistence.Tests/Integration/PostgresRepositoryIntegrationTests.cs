using Microsoft.EntityFrameworkCore;
using Pol33.Core.Identity;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;
using Testcontainers.PostgreSql;

namespace Pol33.Persistence.Tests.Integration;

[Trait("Category", "Docker")]
public sealed class PostgresRepositoryIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _postgres.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [Fact]
    public async Task MigrationsApply_TenantRepositoryRoundTrip_Succeeds()
    {
        _postgres.Should().NotBeNull();

        await using var db = PersistenceTestDbContextFactory.CreateNpgsql(_postgres!.GetConnectionString());
        await db.Database.MigrateAsync();

        var sut = new TenantRepository(db);
        var now = DateTimeOffset.UtcNow;
        var tenant = new TenantRecord(
            Guid.NewGuid(),
            "docker-tenant",
            "Docker Tenant",
            null,
            null,
            true,
            now,
            now);

        await sut.CreateAsync(tenant);

        var loaded = await sut.GetBySlugAsync("docker-tenant");
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Docker Tenant");
    }
}
