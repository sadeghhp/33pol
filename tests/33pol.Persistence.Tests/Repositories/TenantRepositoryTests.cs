using Pol33.Core.Identity;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class TenantRepositoryTests
{
    [Fact]
    public async Task CreateAsync_ThenGetBySlug_ReturnsTenant()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(CreateAsync_ThenGetBySlug_ReturnsTenant));
        var sut = new TenantRepository(db);
        var now = DateTimeOffset.UtcNow;
        var tenant = new TenantRecord(
            Guid.NewGuid(),
            "acme",
            "Acme Corp",
            "standard",
            "cc-1",
            true,
            now,
            now);

        await sut.CreateAsync(tenant);

        var loaded = await sut.GetBySlugAsync("acme");

        loaded.Should().NotBeNull();
        loaded!.Slug.Should().Be("acme");
        loaded.Name.Should().Be("Acme Corp");
        loaded.PlanSlug.Should().Be("standard");
    }

    [Fact]
    public async Task ListActiveAsync_ExcludesInactiveTenants()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(ListActiveAsync_ExcludesInactiveTenants));
        var sut = new TenantRepository(db);
        var now = DateTimeOffset.UtcNow;

        await sut.CreateAsync(new TenantRecord(Guid.NewGuid(), "active", "Active", null, null, true, now, now));
        await sut.CreateAsync(new TenantRecord(Guid.NewGuid(), "inactive", "Inactive", null, null, false, now, now));

        var active = await sut.ListActiveAsync();

        active.Should().ContainSingle(t => t.Slug == "active");
    }
}
