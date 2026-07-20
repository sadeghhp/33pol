using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Billing;
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

    [Fact]
    public async Task CreateAsync_WithMetadata_PersistsOnList()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest
                {
                    Role = ApiKeyRole.Inference,
                    Label = "bot",
                    Assignee = "Platform",
                    CostCenter = "eng",
                });

            var list = await sut.ListAsync(tenantId);
            var item = list.Single(x => x.Id == created.Id);
            item.Label.Should().Be("bot");
            item.Assignee.Should().Be("Platform");
            item.CostCenter.Should().Be("eng");
        }
    }

    [Fact]
    public async Task UpdateAsync_RevokedKey_Throws()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });
            await sut.RevokeAsync(tenantId, created.Id);

            var act = () => sut.UpdateAsync(
                tenantId,
                created.Id,
                new UpdateAdminApiKeyRequest { Label = "x" });

            await act.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task RevokeManyAsync_RevokesExistingTenantKeys_AndSkipsInvalidIds()
    {
        var (sut, tenantId, validator, db) = await CreateSutAsync();
        await using (db)
        {
            var first = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });
            var second = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });

            var revokedCount = await sut.RevokeManyAsync(
                tenantId,
                [first.Id, second.Id, Guid.Empty, first.Id, Guid.NewGuid()]);

            revokedCount.Should().Be(2);
            (await validator.ValidateAsync(first.Secret, CancellationToken.None)).IsSuccess.Should().BeFalse();
            (await validator.ValidateAsync(second.Secret, CancellationToken.None)).IsSuccess.Should().BeFalse();
        }
    }

    [Fact]
    public async Task RevokeManyAsync_EmptyKeyIds_ReturnsZero()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var revokedCount = await sut.RevokeManyAsync(tenantId, []);

            revokedCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task UpdateAsync_UpdatesMetadata()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference, Label = "before" });

            var updated = await sut.UpdateAsync(
                tenantId,
                created.Id,
                new UpdateAdminApiKeyRequest
                {
                    Label = "after",
                    Assignee = "Platform",
                    Description = "desc",
                    CostCenter = "eng",
                });

            updated.Label.Should().Be("after");
            updated.Assignee.Should().Be("Platform");
            updated.Description.Should().Be("desc");
            updated.CostCenter.Should().Be("eng");
        }
    }

    [Fact]
    public async Task ListAsync_IncludeUsageSummary_AttachesSummaries()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference, CostCenter = "eng" });

            var billingEvents = new BillingEventRepository(db);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await billingEvents.TryAppendAsync(new BillingEventRecord(
                Guid.NewGuid(),
                "req-1",
                tenantId,
                created.Id,
                "gpt-4o",
                "eng",
                10,
                5,
                null,
                null,
                0.10m,
                100,
                DateTimeOffset.UtcNow));

            var list = await sut.ListAsync(tenantId, includeUsageSummary: true);
            var item = list.Single(x => x.Id == created.Id);
            item.UsageSummary.Should().NotBeNull();
            item.UsageSummary!.RequestCount.Should().Be(1);
            item.UsageSummary.PromptTokens.Should().Be(10);
        }
    }

    [Fact]
    public async Task GetUsageAsync_ReturnsSummaryAndEvents()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest
                {
                    Role = ApiKeyRole.Inference,
                    Label = "ops",
                    Assignee = "Team",
                    CostCenter = "eng",
                });

            var billingEvents = new BillingEventRepository(db);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await billingEvents.TryAppendAsync(new BillingEventRecord(
                Guid.NewGuid(),
                "req-usage",
                tenantId,
                created.Id,
                "gpt-4o",
                "eng",
                12,
                6,
                null,
                null,
                0.12m,
                120,
                DateTimeOffset.UtcNow));

            var usage = await sut.GetUsageAsync(tenantId, created.Id, today, today);

            usage.Id.Should().Be(created.Id);
            usage.KeyPrefix.Should().Be(created.KeyPrefix);
            usage.Label.Should().Be("ops");
            usage.Assignee.Should().Be("Team");
            usage.CostCenter.Should().Be("eng");
            usage.FromDate.Should().Be(today);
            usage.ToDate.Should().Be(today);
            usage.Summary.RequestCount.Should().Be(1);
            usage.Events.Should().ContainSingle(e => e.RequestId == "req-usage");
        }
    }

    [Fact]
    public async Task GetUsageAsync_WrongTenant_ThrowsUnauthorizedAccessException()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });

            var act = () => sut.GetUsageAsync(Guid.NewGuid(), created.Id, null, null);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }
    }

    [Fact]
    public async Task GetUsageAsync_MissingKey_ThrowsKeyNotFoundException()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var act = () => sut.GetUsageAsync(tenantId, Guid.NewGuid(), null, null);

            await act.Should().ThrowAsync<KeyNotFoundException>();
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
        var billingEvents = new BillingEventRepository(db);
        var sut = new AdminKeyService(apiKeys, validator, billingEvents, securityOptions);
        return (sut, tenantId, validator, db);
    }
}
