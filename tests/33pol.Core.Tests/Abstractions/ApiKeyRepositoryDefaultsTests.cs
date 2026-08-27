using Pol33.Core.Abstractions;
using Pol33.Core.Identity;

namespace Pol33.Core.Tests.Abstractions;

/// <summary>
/// The read-only members of <see cref="IApiKeyRepository"/> default to "nothing found", which is an
/// honest answer. The mutating ones must not: a no-op that reports success lets a caller append a
/// lifecycle event, write an audit entry and return 204 over a key it never touched.
/// </summary>
public sealed class ApiKeyRepositoryDefaultsTests
{
    public static TheoryData<string, Func<IApiKeyRepository, Task>> UnimplementedWrites => new()
    {
        { nameof(IApiKeyRepository.ArchiveAsync), r => r.ArchiveAsync(Guid.NewGuid(), DateTimeOffset.UtcNow) },
        { nameof(IApiKeyRepository.UnarchiveAsync), r => r.UnarchiveAsync(Guid.NewGuid()) },
        { nameof(IApiKeyRepository.RestoreRevokedAsync), r => r.RestoreRevokedAsync(Guid.NewGuid()) },
        { nameof(IApiKeyRepository.DeleteAsync), r => r.DeleteAsync(Guid.NewGuid()) },
        { nameof(IApiKeyRepository.CountActiveAdminKeysAsync), r => r.CountActiveAdminKeysAsync(Guid.NewGuid()) },
    };

    [Theory]
    [MemberData(nameof(UnimplementedWrites))]
    public void UnimplementedLifecycleMember_Throws(string member, Func<IApiKeyRepository, Task> call)
    {
        IApiKeyRepository repository = new BarelyImplementedApiKeyRepository();
        // Synchronously, not as a faulted task: the default bodies throw before they ever return one.
        Action act = () => _ = call(repository);

        act.Should().Throw<NotSupportedException>()
            .WithMessage($"*{member}*", "the message has to name what is missing");
    }

    [Fact]
    public async Task UnimplementedReads_StillDegradeToNothingFound()
    {
        IApiKeyRepository sut = new BarelyImplementedApiKeyRepository();

        (await sut.ListExpiringAsync(DateTimeOffset.UtcNow)).Should().BeEmpty();
        (await sut.ListIdleAsync(DateTimeOffset.UtcNow)).Should().BeEmpty();
        (await sut.CountAsync(CancellationToken.None)).Should().Be((0, 0, 0));
    }

    /// <summary>Implements only the members that have no default, as a partial adopter would.</summary>
    private sealed class BarelyImplementedApiKeyRepository : IApiKeyRepository
    {
        public Task<ApiKeyRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ApiKeyRecord?>(null);

        public Task<IReadOnlyList<ApiKeyRecord>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ApiKeyRecord>>([]);

        public Task<ApiKeyRecord?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) =>
            Task.FromResult<ApiKeyRecord?>(null);

        public Task<IReadOnlyList<ApiKeyRecord>> FindByPrefixesAsync(IReadOnlyCollection<string> keyPrefixes, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ApiKeyRecord>>([]);

        public Task<IReadOnlyList<ApiKeyRecord>> ListByTenantAsync(Guid tenantId, bool includeArchived = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ApiKeyRecord>>([]);

        public Task<ApiKeyRecord> CreateAsync(ApiKeyRecord apiKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(apiKey);

        public Task RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ApiKeyRecord> UpdateMetadataAsync(Guid id, ApiKeyMetadataUpdate update, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task TouchLastUsedAsync(Guid id, DateTimeOffset atUtc, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
