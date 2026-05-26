using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Persistence;
using Pol33.Persistence.Repositories;
using TenantRepository = Pol33.Persistence.Repositories.TenantRepository;
using Pol33.Security.Configuration;
using Pol33.Security.Services;

namespace Pol33.Security.Tests.Services;

public sealed class AdminKeyServiceTests
{
    private const string Pepper = "test-pepper";

    [Fact]
    public async Task CreateAsync_ReturnsSecretOnce_ListOmitsSecret()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
        var created = await sut.CreateAsync(
            tenantId,
            new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });

        created.Secret.Should().StartWith("sk-33pol-");
        var list = await sut.ListAsync(tenantId);
        list.Should().ContainSingle(item => item.Id == created.Id);
        list.Single().KeyPrefix.Should().Be(created.KeyPrefix);
        }
    }

    [Fact]
    public async Task RevokeAsync_InvalidatesSubsequentValidation()
    {
        var (sut, tenantId, validator, db) = await CreateSutAsync();
        await using (db)
        {
        var created = await sut.CreateAsync(
            tenantId,
            new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });

        (await validator.ValidateAsync(created.Secret, CancellationToken.None)).IsSuccess.Should().BeTrue();

        await sut.RevokeAsync(tenantId, created.Id);

        (await validator.ValidateAsync(created.Secret, CancellationToken.None)).IsSuccess.Should().BeFalse();
        }
    }

    private static async Task<(AdminKeyService Sut, Guid TenantId, ApiKeyValidator Validator, GatewayDbContext Db)> CreateSutAsync()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new GatewayDbContext(options);

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Persistence.Entities.TenantEntity
        {
            Id = tenantId,
            Slug = "t1",
            Name = "Tenant",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var apiKeys = new ApiKeyRepository(db);
        var tenants = new TenantRepository(db);
        var securityOptions = Options.Create(new GatewaySecurityOptions { KeyPepper = Pepper });
        var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var validator = new ApiKeyValidator(apiKeys, tenants, memoryCache, securityOptions);
        var sut = new AdminKeyService(apiKeys, validator, securityOptions);
        return (sut, tenantId, validator, db);
    }
}
