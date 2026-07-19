using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Persistence.Bootstrap;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Bootstrap;

public sealed class GatewayDbBootstrapTests
{
    [Fact]
    public async Task EnsureInitializedAsync_EmptyDatabaseWithAdminKey_CreatesTenantAndKey()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(
            nameof(EnsureInitializedAsync_EmptyDatabaseWithAdminKey_CreatesTenantAndKey));
        await db.Database.EnsureCreatedAsync();

        var options = Options.Create(new GatewayBootstrapOptions
        {
            Enabled = true,
            TenantSlug = "default",
            TenantName = "Default",
            AdminApiKey = "sk-33pol-bootstrap-test",
            KeyPepper = "test-pepper",
        });

        var sut = new GatewayDbBootstrap(
            db,
            options,
            Options.Create(new Pol33.Core.Configuration.GatewayOptions()),
            NullLogger<GatewayDbBootstrap>.Instance);

        await sut.EnsureInitializedAsync();

        var tenants = await db.Tenants.Include(t => t.ApiKeys).ToListAsync();
        tenants.Should().ContainSingle();
        tenants[0].ApiKeys.Should().ContainSingle(k => k.Role == Pol33.Core.Identity.ApiKeyRole.Admin);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ExistingTenant_SkipsBootstrap()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(
            nameof(EnsureInitializedAsync_ExistingTenant_SkipsBootstrap));
        await db.Database.EnsureCreatedAsync();

        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new Pol33.Persistence.Entities.TenantEntity
        {
            Id = Guid.NewGuid(),
            Slug = "existing",
            Name = "Existing",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var options = Options.Create(new GatewayBootstrapOptions
        {
            Enabled = true,
            AdminApiKey = "sk-should-not-create",
        });

        var sut = new GatewayDbBootstrap(
            db,
            options,
            Options.Create(new Pol33.Core.Configuration.GatewayOptions()),
            NullLogger<GatewayDbBootstrap>.Instance);

        await sut.EnsureInitializedAsync();

        (await db.ApiKeys.CountAsync()).Should().Be(0);
    }
}
