using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Security;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Security;
using Pol33.Security.Configuration;
using Pol33.Security.Services;

namespace Pol33.Security.Tests.Services;

public sealed class ApiKeyValidatorTests
{
    private const string Pepper = "test-pepper";

    [Fact]
    public async Task ValidateAsync_ValidKey_ReturnsSuccess()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        const string secret = "sk-33pol-valid-key-001";
        await SeedKeyAsync(db, tenantId, secret, ApiKeyRole.Inference);

        var sut = CreateValidator(db);
        var result = await sut.ValidateAsync(secret);

        result.IsSuccess.Should().BeTrue();
        result.TenantId.Should().Be(tenantId);
        result.Role.Should().Be(ApiKeyRole.Inference);
    }

    [Fact]
    public async Task ValidateAsync_TenantWithCostCenter_ReturnsCostCenter()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db, costCenter: "eng-platform");
        const string secret = "sk-33pol-cost-center-key";
        await SeedKeyAsync(db, tenantId, secret, ApiKeyRole.Inference);

        var sut = CreateValidator(db);
        var result = await sut.ValidateAsync(secret);

        result.IsSuccess.Should().BeTrue();
        result.CostCenter.Should().Be("eng-platform");
    }

    [Fact]
    public async Task ValidateAsync_KeyCostCenterOverride_WinsOverTenant()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db, costCenter: "tenant-cc");
        const string secret = "sk-33pol-key-cost-center";
        await SeedKeyAsync(db, tenantId, secret, ApiKeyRole.Inference, keyCostCenter: "key-cc");

        var sut = CreateValidator(db);
        var result = await sut.ValidateAsync(secret);

        result.IsSuccess.Should().BeTrue();
        result.CostCenter.Should().Be("key-cc");
    }

    [Fact]
    public async Task ValidateAsync_KeyWithoutCostCenter_FallsBackToTenant()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db, costCenter: "tenant-cc");
        const string secret = "sk-33pol-key-fallback";
        await SeedKeyAsync(db, tenantId, secret, ApiKeyRole.Inference);

        var sut = CreateValidator(db);
        var result = await sut.ValidateAsync(secret);

        result.IsSuccess.Should().BeTrue();
        result.CostCenter.Should().Be("tenant-cc");
    }

    [Fact]
    public async Task ValidateAsync_RevokedKey_ReturnsRevokedFailure()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        const string secret = "sk-33pol-revoked-key";
        var keyId = await SeedKeyAsync(db, tenantId, secret, ApiKeyRole.Inference, revoked: true);

        var sut = CreateValidator(db);
        var result = await sut.ValidateAsync(secret);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(ApiKeyValidationFailure.Revoked);
    }

    [Fact]
    public async Task ValidateAsync_ExpiredKey_ReturnsExpiredFailure()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        const string secret = "sk-33pol-expired-key";
        await SeedKeyAsync(db, tenantId, secret, ApiKeyRole.Inference, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var sut = CreateValidator(db);
        var result = await sut.ValidateAsync(secret);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(ApiKeyValidationFailure.Expired);
    }

    [Fact]
    public async Task InvalidateCache_AfterRevoke_ForcesRevalidation()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        const string secret = "sk-33pol-cache-key";
        var keyId = await SeedKeyAsync(db, tenantId, secret, ApiKeyRole.Inference);

        var sut = CreateValidator(db);
        (await sut.ValidateAsync(secret)).IsSuccess.Should().BeTrue();

        var entity = await db.ApiKeys.FindAsync(keyId);
        entity!.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        sut.InvalidateCache(keyId);

        (await sut.ValidateAsync(secret)).Failure.Should().Be(ApiKeyValidationFailure.Revoked);
    }

    private static ApiKeyValidator CreateValidator(Pol33.Persistence.GatewayDbContext db) =>
        new(
            new ApiKeyRepository(db),
            new TenantRepository(db),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new GatewaySecurityOptions { KeyPepper = Pepper }));

    private static Pol33.Persistence.GatewayDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<Pol33.Persistence.GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new Pol33.Persistence.GatewayDbContext(options);
    }

    private static async Task<Guid> SeedTenantAsync(
        Pol33.Persistence.GatewayDbContext db,
        string? costCenter = null)
    {
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Pol33.Persistence.Entities.TenantEntity
        {
            Id = tenantId,
            Slug = "tenant-a",
            Name = "Tenant A",
            CostCenter = costCenter,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return tenantId;
    }

    private static async Task<Guid> SeedKeyAsync(
        Pol33.Persistence.GatewayDbContext db,
        Guid tenantId,
        string secret,
        ApiKeyRole role,
        bool revoked = false,
        DateTimeOffset? expiresAt = null,
        string? keyCostCenter = null)
    {
        var keyId = Guid.NewGuid();
        db.ApiKeys.Add(new Pol33.Persistence.Entities.ApiKeyEntity
        {
            Id = keyId,
            TenantId = tenantId,
            KeyHash = ApiKeyHashing.Hash(secret, Pepper),
            KeyPrefix = ApiKeyHashing.CreatePrefix(secret),
            Role = role,
            CostCenter = keyCostCenter,
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = revoked ? DateTimeOffset.UtcNow : null,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync();
        return keyId;
    }
}
