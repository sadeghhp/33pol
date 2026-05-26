using Microsoft.EntityFrameworkCore;
using Pol33.Core.Identity;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests;

public sealed class GatewayDbContextTests
{
    [Fact]
    public async Task Model_CreatesTenantsApiKeysAndModelGrantsTables()
    {
        await using var db = await SqliteGatewayDbContextFactory.CreateAsync();

        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Slug = "acme",
            Name = "Acme Corp",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Tenants.Add(tenant);
        db.ApiKeys.Add(new ApiKeyEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Tenant = tenant,
            KeyHash = "hash",
            KeyPrefix = "sk-33pol",
            Role = ApiKeyRole.Inference,
            Scopes = ["inference"],
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.ModelGrants.Add(new ModelGrantEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Tenant = tenant,
            ModelPattern = "canonical-model",
            Effect = GrantEffect.Allow,
        });

        await db.SaveChangesAsync();

        (await db.Tenants.CountAsync()).Should().Be(1);
        (await db.ApiKeys.CountAsync()).Should().Be(1);
        (await db.ModelGrants.CountAsync()).Should().Be(1);
    }
}
