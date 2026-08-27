using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.OperatorConsole.Commands;

namespace Pol33.OperatorConsole.Tests.Commands;

public sealed class OperatorConsoleKeysInteractorTests
{
    [Fact]
    public void BuildKeysTable_WithKeys_HasOneDataRow()
    {
        var keys = new List<AdminApiKeyListItem>
        {
            new()
            {
                Id = Guid.NewGuid(),
                KeyPrefix = "sk-abc12",
                Role = ApiKeyRole.Admin,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        OperatorConsoleKeysInteractor.BuildKeysTable(keys).Rows.Count.Should().Be(1);
    }

    [Fact]
    public void BuildKeysTable_Empty_HasPlaceholderRow()
    {
        OperatorConsoleKeysInteractor.BuildKeysTable([]).Rows.Count.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(StatusCases))]
    public void DescribeStatus_NamesTheKeysCurrentState(AdminApiKeyListItem key, string expected)
    {
        OperatorConsoleKeysInteractor.DescribeStatus(key).Should().Be(expected);
    }

    public static TheoryData<AdminApiKeyListItem, string> StatusCases()
    {
        var now = DateTimeOffset.UtcNow;

        return new TheoryData<AdminApiKeyListItem, string>
        {
            { Key(now), "active" },
            { Key(now, expiresAt: now.AddDays(1)), "active" },
            { Key(now, expiresAt: now.AddDays(-1)), "expired" },
            { Key(now, revokedAt: now), "revoked" },
            // Archived wins: an archived key is always revoked, and "archived" is the more specific fact.
            { Key(now, revokedAt: now, archivedAt: now), "archived" },
        };
    }

    private static AdminApiKeyListItem Key(
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null,
        DateTimeOffset? archivedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            KeyPrefix = "sk-abc12",
            Role = ApiKeyRole.Inference,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
            ArchivedAt = archivedAt,
        };

    [Fact]
    public void AdminApiKeyListItem_DoesNotExposeSecretProperty()
    {
        typeof(AdminApiKeyListItem).GetProperty("Secret").Should().BeNull();
    }

    [Fact]
    public async Task ListKeysAsync_UnknownTenant_WritesWithoutThrowing()
    {
        var interactor = new OperatorConsoleKeysInteractor(
            new FakeAdminKeyService(),
            new FakeTenantRepository(null),
            Options.Create(new OperatorConsoleOptions { TenantSlug = "missing" }));

        var act = () => interactor.ListKeysAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private sealed class FakeAdminKeyService : IAdminKeyService
    {
        public Task<AdminApiKeyCreatedResponse> CreateAsync(
            Guid tenantId,
            CreateAdminApiKeyRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AdminApiKeyListItem>> ListAsync(
            Guid tenantId,
            bool includeUsageSummary = false,
            bool includeArchived = false,
            Guid? actorKeyId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AdminApiKeyListItem>>([]);

        public Task<AdminApiKeyListItem> UpdateAsync(
            Guid tenantId,
            Guid keyId,
            UpdateAdminApiKeyRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdminApiKeyUsageResponse> GetUsageAsync(
            Guid tenantId,
            Guid keyId,
            DateOnly? fromDate,
            DateOnly? toDate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeAsync(
            Guid tenantId,
            Guid keyId,
            Guid? actorKeyId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> RevokeManyAsync(
            Guid tenantId,
            IReadOnlyCollection<Guid> keyIds,
            Guid? actorKeyId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task ArchiveAsync(
            Guid tenantId,
            Guid keyId,
            Guid? actorKeyId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UnarchiveAsync(
            Guid tenantId,
            Guid keyId,
            Guid? actorKeyId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdminApiKeyListItem> DeleteAsync(
            Guid tenantId,
            Guid keyId,
            Guid? actorKeyId,
            string? confirmKeyPrefix,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdminApiKeyLifecycleResponse> GetLifecycleAsync(
            Guid tenantId,
            Guid keyId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTenantRepository(TenantRecord? tenant) : ITenantRepository
    {
        public Task<TenantRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenant);

        public Task<TenantRecord?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenant);

        public Task<TenantRecord> CreateAsync(TenantRecord tenant, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TenantRecord>> ListActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantRecord>>([]);
    }
}
