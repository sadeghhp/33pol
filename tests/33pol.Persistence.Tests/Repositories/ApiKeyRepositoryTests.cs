using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class ApiKeyRepositoryTests
{
    [Fact]
    public async Task Create_FindByHash_Revoke_Works()
    {
        await using var db = await SqliteGatewayDbContextFactory.CreateAsync();
        ITenantRepository tenants = new TenantRepository(db);
        IApiKeyRepository keys = new ApiKeyRepository(db);

        var tenant = await tenants.CreateAsync(new CreateTenantRequest
        {
            Slug = "acme",
            Name = "Acme Corp",
        });

        var created = await keys.CreateAsync(new CreateApiKeyRequest
        {
            TenantId = tenant.Id,
            KeyHash = "abc123hash",
            KeyPrefix = "sk-33pol-ab",
            Role = ApiKeyRole.Inference,
            Scopes = ["inference"],
        });

        var found = await keys.FindByKeyHashAsync("abc123hash");
        found.Should().NotBeNull();
        found!.TenantSlug.Should().Be("acme");
        found.IsActive.Should().BeTrue();

        (await keys.RevokeAsync(created.Id)).Should().BeTrue();

        var revoked = await keys.GetByIdAsync(created.Id);
        revoked!.IsActive.Should().BeFalse();
        (await keys.RevokeAsync(created.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task ListByTenantId_ReturnsAllKeys()
    {
        await using var db = await SqliteGatewayDbContextFactory.CreateAsync();
        ITenantRepository tenants = new TenantRepository(db);
        IApiKeyRepository keys = new ApiKeyRepository(db);

        var tenant = await tenants.CreateAsync(new CreateTenantRequest
        {
            Slug = "acme",
            Name = "Acme Corp",
        });

        await keys.CreateAsync(new CreateApiKeyRequest
        {
            TenantId = tenant.Id,
            KeyHash = "hash-1",
            KeyPrefix = "sk-1",
            Role = ApiKeyRole.Inference,
        });
        await keys.CreateAsync(new CreateApiKeyRequest
        {
            TenantId = tenant.Id,
            KeyHash = "hash-2",
            KeyPrefix = "sk-2",
            Role = ApiKeyRole.Admin,
        });

        var list = await keys.ListByTenantIdAsync(tenant.Id);

        list.Should().HaveCount(2);
        list.Select(k => k.Role).Should().Contain([ApiKeyRole.Inference, ApiKeyRole.Admin]);
    }
}
