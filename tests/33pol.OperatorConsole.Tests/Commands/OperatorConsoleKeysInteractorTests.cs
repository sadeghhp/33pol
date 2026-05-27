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
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AdminApiKeyListItem>>([]);

        public Task RevokeAsync(Guid tenantId, Guid keyId, CancellationToken cancellationToken = default) =>
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
