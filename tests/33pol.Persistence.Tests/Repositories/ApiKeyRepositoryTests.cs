using Pol33.Core.Identity;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class ApiKeyRepositoryTests
{
    [Fact]
    public async Task CreateAsync_ThenFindByPrefix_ReturnsKey()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(CreateAsync_ThenFindByPrefix_ReturnsKey));
        var tenantRepo = new TenantRepository(db);
        var sut = new ApiKeyRepository(db);
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();

        await tenantRepo.CreateAsync(new TenantRecord(tenantId, "t1", "Tenant 1", null, null, true, now, now));

        var key = new ApiKeyRecord(
            Guid.NewGuid(),
            tenantId,
            "hash-value",
            "sk-33pol-abc",
            ApiKeyRole.Inference,
            ["inference"],
            null,
            null,
            now,
            null);

        await sut.CreateAsync(key);

        var loaded = await sut.FindByPrefixAsync("sk-33pol-abc");

        loaded.Should().NotBeNull();
        loaded!.Role.Should().Be(ApiKeyRole.Inference);
        loaded.Scopes.Should().Contain("inference");
    }

    [Fact]
    public async Task FindByPrefixesAsync_MixedPrefixFormats_ReturnsEveryMatch()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(FindByPrefixesAsync_MixedPrefixFormats_ReturnsEveryMatch));
        var tenantRepo = new TenantRepository(db);
        var sut = new ApiKeyRepository(db);
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();

        await tenantRepo.CreateAsync(new TenantRecord(tenantId, "t1", "Tenant 1", null, null, true, now, now));
        await sut.CreateAsync(CreateKey(tenantId, "legacy-hash", "sk-33pol-797", now));
        await sut.CreateAsync(CreateKey(tenantId, "current-hash", "sk-33pol-797a8b0b67b", now));
        await sut.CreateAsync(CreateKey(tenantId, "other-hash", "sk-33pol-000", now));

        var loaded = await sut.FindByPrefixesAsync(["sk-33pol-797a8b0b67b", "sk-33pol-797"]);

        loaded.Select(k => k.KeyHash).Should().BeEquivalentTo(["legacy-hash", "current-hash"]);
    }

    [Fact]
    public async Task FindByPrefixesAsync_EmptyPrefixes_ReturnsEmpty()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(FindByPrefixesAsync_EmptyPrefixes_ReturnsEmpty));
        var sut = new ApiKeyRepository(db);

        var loaded = await sut.FindByPrefixesAsync([]);

        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task FindByPrefixesAsync_NullPrefixes_Throws()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(FindByPrefixesAsync_NullPrefixes_Throws));
        var sut = new ApiKeyRepository(db);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.FindByPrefixesAsync(null!));
    }

    private static ApiKeyRecord CreateKey(Guid tenantId, string hash, string prefix, DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            tenantId,
            hash,
            prefix,
            ApiKeyRole.Inference,
            ["inference"],
            null,
            null,
            now,
            null);

    [Fact]
    public async Task RevokeAsync_SetsRevokedAt()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(RevokeAsync_SetsRevokedAt));
        var tenantRepo = new TenantRepository(db);
        var sut = new ApiKeyRepository(db);
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var keyId = Guid.NewGuid();

        await tenantRepo.CreateAsync(new TenantRecord(tenantId, "t1", "Tenant 1", null, null, true, now, now));
        await sut.CreateAsync(new ApiKeyRecord(
            keyId,
            tenantId,
            "hash",
            "sk-prefix",
            ApiKeyRole.Admin,
            [],
            null,
            null,
            now,
            null));

        var revokedAt = now.AddMinutes(5);
        await sut.RevokeAsync(keyId, revokedAt);

        var loaded = await sut.GetByIdAsync(keyId);
        loaded!.RevokedAt.Should().Be(revokedAt);
    }

    [Fact]
    public async Task UpdateMetadataAsync_PersistsFields()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(UpdateMetadataAsync_PersistsFields));
        var tenantRepo = new TenantRepository(db);
        var sut = new ApiKeyRepository(db);
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var keyId = Guid.NewGuid();

        await tenantRepo.CreateAsync(new TenantRecord(tenantId, "t1", "Tenant 1", null, null, true, now, now));
        await sut.CreateAsync(new ApiKeyRecord(
            keyId,
            tenantId,
            "hash",
            "sk-prefix",
            ApiKeyRole.Inference,
            [],
            null,
            null,
            now,
            null));

        var updated = await sut.UpdateMetadataAsync(
            keyId,
            new ApiKeyMetadataUpdate("prod-bot", "Platform team", "Notes", "eng-platform"));

        updated.Label.Should().Be("prod-bot");
        updated.Assignee.Should().Be("Platform team");
        updated.Description.Should().Be("Notes");
        updated.CostCenter.Should().Be("eng-platform");
    }

    [Fact]
    public async Task TouchLastUsedAsync_SetsTimestamp()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(TouchLastUsedAsync_SetsTimestamp));
        var tenantRepo = new TenantRepository(db);
        var sut = new ApiKeyRepository(db);
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var touchedAt = now.AddHours(1);

        await tenantRepo.CreateAsync(new TenantRecord(tenantId, "t1", "Tenant 1", null, null, true, now, now));
        await sut.CreateAsync(new ApiKeyRecord(
            keyId,
            tenantId,
            "hash",
            "sk-prefix",
            ApiKeyRole.Inference,
            [],
            null,
            null,
            now,
            null));

        await sut.TouchLastUsedAsync(keyId, touchedAt);

        var loaded = await sut.GetByIdAsync(keyId);
        loaded!.LastUsedAt.Should().Be(touchedAt);
    }
}
