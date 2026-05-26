using Pol33.Core.Identity;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class ModelGrantRepositoryTests
{
    [Fact]
    public async Task AddAsync_ThenListByTenant_ReturnsGrant()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(AddAsync_ThenListByTenant_ReturnsGrant));
        var tenantRepo = new TenantRepository(db);
        var sut = new ModelGrantRepository(db);
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();

        await tenantRepo.CreateAsync(new TenantRecord(tenantId, "t1", "Tenant 1", null, null, true, now, now));

        await sut.AddAsync(new ModelGrantRecord(
            Guid.NewGuid(),
            tenantId,
            "gpt-4",
            GrantEffect.Allow));

        var grants = await sut.ListByTenantAsync(tenantId);

        grants.Should().ContainSingle(g => g.ModelPattern == "gpt-4");
    }
}
