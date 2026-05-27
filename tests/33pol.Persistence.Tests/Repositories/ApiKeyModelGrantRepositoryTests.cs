using Pol33.Core.Identity;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class ApiKeyModelGrantRepositoryTests
{
    [Fact]
    public async Task ReplaceForApiKeyAsync_ReplacesAllowlist()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(ReplaceForApiKeyAsync_ReplacesAllowlist));
        var tenantRepo = new TenantRepository(db);
        var apiKeyRepo = new ApiKeyRepository(db);
        var sut = new ApiKeyModelGrantRepository(db);
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var keyId = Guid.NewGuid();

        await tenantRepo.CreateAsync(new TenantRecord(tenantId, "t1", "Tenant", null, null, true, now, now));
        await apiKeyRepo.CreateAsync(
            new ApiKeyRecord(keyId, tenantId, "hash", "sk-prefix", ApiKeyRole.Inference, [], null, null, now, null));

        await sut.ReplaceForApiKeyAsync(keyId, ["model-a", "model-b"]);

        var grants = await sut.ListByApiKeyAsync(keyId);
        grants.Select(g => g.ModelPattern).Should().BeEquivalentTo(["model-a", "model-b"]);

        await sut.ReplaceForApiKeyAsync(keyId, ["model-c"]);
        grants = await sut.ListByApiKeyAsync(keyId);
        grants.Should().ContainSingle(g => g.ModelPattern == "model-c");
    }
}
