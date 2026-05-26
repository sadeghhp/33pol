using Pol33.Core.Abstractions;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class TenantRepositoryTests
{
    [Fact]
    public async Task CreateAsync_ThenGetBySlug_ReturnsTenant()
    {
        await using var db = await SqliteGatewayDbContextFactory.CreateAsync();
        ITenantRepository repository = new TenantRepository(db);

        var created = await repository.CreateAsync(new CreateTenantRequest
        {
            Slug = "acme",
            Name = "Acme Corp",
            PlanSlug = "standard",
        });

        var loaded = await repository.GetBySlugAsync("acme");

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(created.Id);
        loaded.Slug.Should().Be("acme");
        loaded.PlanSlug.Should().Be("standard");
        loaded.IsActive.Should().BeTrue();
    }
}
