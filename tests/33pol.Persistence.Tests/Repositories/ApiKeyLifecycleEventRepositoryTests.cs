using Pol33.Core.Identity;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class ApiKeyLifecycleEventRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static ApiKeyLifecycleEventRecord Event(
        Guid keyId,
        Guid tenantId,
        ApiKeyLifecycleEvent kind,
        DateTimeOffset occurredAt) =>
        new(Guid.NewGuid(), keyId, tenantId, "sk-33pol-abcd", kind, occurredAt, "billing pipeline");

    [Fact]
    public async Task AppendAsync_ThenListForKey_ReturnsOldestFirst()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(AppendAsync_ThenListForKey_ReturnsOldestFirst));
        var sut = new ApiKeyLifecycleEventRepository(db);
        var keyId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        // Appended out of order on purpose: the reader, not the writer, decides the order.
        await sut.AppendAsync(Event(keyId, tenantId, ApiKeyLifecycleEvent.Archived, Now.AddDays(2)));
        await sut.AppendAsync(Event(keyId, tenantId, ApiKeyLifecycleEvent.Created, Now));
        await sut.AppendAsync(Event(keyId, tenantId, ApiKeyLifecycleEvent.Revoked, Now.AddDays(1)));

        var history = await sut.ListForKeyAsync(tenantId, keyId);

        history.Select(e => e.Event).Should().Equal([
            ApiKeyLifecycleEvent.Created,
            ApiKeyLifecycleEvent.Revoked,
            ApiKeyLifecycleEvent.Archived,
        ]);
        history[0].Label.Should().Be("billing pipeline");
        history[0].KeyPrefix.Should().Be("sk-33pol-abcd");
    }

    [Fact]
    public async Task ListForKeyAsync_OtherTenant_ReturnsNothing()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(ListForKeyAsync_OtherTenant_ReturnsNothing));
        var sut = new ApiKeyLifecycleEventRepository(db);
        var keyId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        await sut.AppendAsync(Event(keyId, tenantId, ApiKeyLifecycleEvent.Created, Now));

        // For a deleted key there is no api_keys row left to check ownership against, so this pair is
        // the only thing keeping one tenant's history out of another tenant's reach.
        (await sut.ListForKeyAsync(Guid.NewGuid(), keyId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Events_SurviveThePermanentDeletionOfTheirKey()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(Events_SurviveThePermanentDeletionOfTheirKey));
        var keys = new ApiKeyRepository(db);
        var sut = new ApiKeyLifecycleEventRepository(db);
        var tenantId = Guid.NewGuid();
        var keyId = Guid.NewGuid();

        db.Tenants.Add(new TenantEntity { Id = tenantId, Slug = "acme", Name = "Acme", CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync();

        await keys.CreateAsync(new ApiKeyRecord(
            keyId, tenantId, "hash", "sk-33pol-abcd", ApiKeyRole.Inference, [], null, null, Now, null));

        await sut.AppendAsync(Event(keyId, tenantId, ApiKeyLifecycleEvent.Created, Now));
        await sut.AppendAsync(Event(keyId, tenantId, ApiKeyLifecycleEvent.Revoked, Now.AddDays(1)));
        await sut.AppendAsync(Event(keyId, tenantId, ApiKeyLifecycleEvent.Deleted, Now.AddDays(2)));

        await keys.DeleteAsync(keyId);

        // This is the whole reason the table carries no foreign key: the record of a credential that
        // once existed has to outlive the credential.
        (await keys.GetByIdAsync(keyId)).Should().BeNull();

        var history = await sut.ListForKeyAsync(tenantId, keyId);
        history.Should().HaveCount(3);
        history[^1].Event.Should().Be(ApiKeyLifecycleEvent.Deleted);
        history[^1].KeyPrefix.Should().Be("sk-33pol-abcd", "the prefix snapshot is all that names the key now");
    }
}
