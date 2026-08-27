using Pol33.Core.Identity;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class ApiKeyRepositoryQueryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static ApiKeyEntity Key(
        Guid tenantId,
        string prefix,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? lastUsedAt = null,
        DateTimeOffset? revokedAt = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? archivedAt = null,
        ApiKeyRole role = ApiKeyRole.Inference) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        KeyHash = Guid.NewGuid().ToString("N"),
        KeyPrefix = prefix,
        Role = role,
        Scopes = [],
        ExpiresAt = expiresAt,
        RevokedAt = revokedAt,
        CreatedAt = createdAt ?? Now.AddDays(-100),
        LastUsedAt = lastUsedAt,
        Label = prefix,
        ArchivedAt = archivedAt,
    };

    private static async Task<Guid> SeedTenantAsync(Pol33.Persistence.GatewayDbContext db)
    {
        var tenant = new TenantEntity { Id = Guid.NewGuid(), Slug = "acme", Name = "Acme", CreatedAt = Now, UpdatedAt = Now };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    [Fact]
    public async Task ListExpiringAsync_ReturnsActiveKeysExpiringInsideTheWindowOnly()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(ListExpiringAsync_ReturnsActiveKeysExpiringInsideTheWindowOnly));
        var tenant = await SeedTenantAsync(db);
        db.ApiKeys.AddRange(
            Key(tenant, "soon", expiresAt: Now.AddDays(3)),
            Key(tenant, "later", expiresAt: Now.AddDays(30)),
            Key(tenant, "already", expiresAt: Now.AddDays(-1)),
            Key(tenant, "revoked", expiresAt: Now.AddDays(3), revokedAt: Now.AddDays(-2)),
            Key(tenant, "forever"));
        await db.SaveChangesAsync();

        var expiring = await new ApiKeyRepository(db).ListExpiringAsync(Now.AddDays(7));

        expiring.Select(k => k.KeyPrefix).Should().Equal("soon");
    }

    [Fact]
    public async Task ListIdleAsync_UsesLastUseOrCreationAndSkipsRevoked()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(ListIdleAsync_UsesLastUseOrCreationAndSkipsRevoked));
        var tenant = await SeedTenantAsync(db);
        db.ApiKeys.AddRange(
            Key(tenant, "stale", lastUsedAt: Now.AddDays(-60)),
            Key(tenant, "never-used-old", createdAt: Now.AddDays(-45)),
            Key(tenant, "fresh", lastUsedAt: Now.AddHours(-1)),
            Key(tenant, "new-unused", createdAt: Now.AddDays(-1)),
            Key(tenant, "revoked", lastUsedAt: Now.AddDays(-90), revokedAt: Now.AddDays(-80)));
        await db.SaveChangesAsync();

        var idle = await new ApiKeyRepository(db).ListIdleAsync(Now.AddDays(-30));

        idle.Select(k => k.KeyPrefix).Should().BeEquivalentTo(["stale", "never-used-old"]);
    }

    [Fact]
    public async Task CountAsync_ReportsTotalAndRevoked()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(CountAsync_ReportsTotalAndRevoked));
        var tenant = await SeedTenantAsync(db);
        db.ApiKeys.AddRange(Key(tenant, "a"), Key(tenant, "b"), Key(tenant, "c", revokedAt: Now));
        await db.SaveChangesAsync();

        var (total, revoked, archived) = await new ApiKeyRepository(db).CountAsync();

        total.Should().Be(3);
        revoked.Should().Be(1);
        archived.Should().Be(0);
    }

    [Fact]
    public async Task CountAsync_ExcludesArchivedFromTotalAndRevoked()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(CountAsync_ExcludesArchivedFromTotalAndRevoked));
        var tenant = await SeedTenantAsync(db);
        db.ApiKeys.AddRange(
            Key(tenant, "live"),
            Key(tenant, "revoked", revokedAt: Now),
            Key(tenant, "filed-away", revokedAt: Now, archivedAt: Now));
        await db.SaveChangesAsync();

        var (total, revoked, archived) = await new ApiKeyRepository(db).CountAsync();

        // Archiving must shrink the operational headline rather than inflate it — otherwise the
        // Overview count creeps upward as archiving is adopted, which is the opposite of the point.
        total.Should().Be(2);
        revoked.Should().Be(1);
        archived.Should().Be(1);
    }

    [Fact]
    public async Task ListExpiringAsync_ExcludesArchived()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(ListExpiringAsync_ExcludesArchived));
        var tenant = await SeedTenantAsync(db);
        db.ApiKeys.AddRange(
            Key(tenant, "expiring", expiresAt: Now.AddDays(3)),
            Key(tenant, "expiring-but-filed", expiresAt: Now.AddDays(3), archivedAt: Now));
        await db.SaveChangesAsync();

        var expiring = await new ApiKeyRepository(db).ListExpiringAsync(Now.AddDays(7));

        expiring.Select(k => k.KeyPrefix).Should().Equal(["expiring"]);
    }

    [Fact]
    public async Task ListIdleAsync_ExcludesArchived()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(ListIdleAsync_ExcludesArchived));
        var tenant = await SeedTenantAsync(db);
        db.ApiKeys.AddRange(
            Key(tenant, "idle", lastUsedAt: Now.AddDays(-90)),
            Key(tenant, "idle-but-filed", lastUsedAt: Now.AddDays(-90), archivedAt: Now));
        await db.SaveChangesAsync();

        var idle = await new ApiKeyRepository(db).ListIdleAsync(Now.AddDays(-30));

        idle.Select(k => k.KeyPrefix).Should().Equal(["idle"]);
    }

    [Fact]
    public async Task CountActiveAdminKeysAsync_CountsOnlyKeysThatCanStillAuthenticateAsAdmin()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(CountActiveAdminKeysAsync_CountsOnlyKeysThatCanStillAuthenticateAsAdmin));
        var tenant = await SeedTenantAsync(db);
        var otherTenant = await SeedTenantAsync(db);
        db.ApiKeys.AddRange(
            Key(tenant, "admin", role: ApiKeyRole.Admin),
            Key(tenant, "both", role: ApiKeyRole.Both),
            Key(tenant, "inference-only", role: ApiKeyRole.Inference),
            Key(tenant, "admin-revoked", revokedAt: Now, role: ApiKeyRole.Admin),
            Key(tenant, "admin-archived", revokedAt: Now, archivedAt: Now, role: ApiKeyRole.Admin),
            Key(tenant, "admin-expired", expiresAt: Now.AddDays(-1), role: ApiKeyRole.Admin),
            Key(otherTenant, "other-tenant-admin", role: ApiKeyRole.Admin));
        await db.SaveChangesAsync();

        var count = await new ApiKeyRepository(db).CountActiveAdminKeysAsync(tenant);

        count.Should().Be(2, "only the live Admin and Both keys for this tenant can still get in");
    }
}
