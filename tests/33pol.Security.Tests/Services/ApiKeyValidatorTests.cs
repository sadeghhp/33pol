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

    [Fact]
    public async Task ValidateAsync_KeySharingPrefixWithWarmedKey_DoesNotImpersonate()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);

        // Both secrets share the same 20-char prefix but are otherwise different keys.
        const string victimSecret = "sk-33pol-collideXXX-victim-admin";
        const string attackerSecret = "sk-33pol-collideXXX-attacker-guess";
        ApiKeyHashing.CreatePrefix(victimSecret).Should().Be(ApiKeyHashing.CreatePrefix(attackerSecret));

        await SeedKeyAsync(db, tenantId, victimSecret, ApiKeyRole.Admin);

        var sut = CreateValidator(db);
        // Warm the cache with the victim's successful validation.
        (await sut.ValidateAsync(victimSecret)).IsSuccess.Should().BeTrue();

        // The attacker's key shares only the prefix; it must never resolve to the victim's cached result.
        var result = await sut.ValidateAsync(attackerSecret);
        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(ApiKeyValidationFailure.Invalid);
    }

    [Fact]
    public async Task ValidateAsync_KeyStoredWithLegacyShortPrefix_ReturnsSuccess()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        const string secret = "sk-33pol-797a8b0b67b157d4d26dd186e5cc2c84";
        await SeedKeyAsync(db, tenantId, secret, ApiKeyRole.Admin, storedPrefix: secret[..12]);

        var sut = CreateValidator(db);
        var result = await sut.ValidateAsync(secret);

        result.IsSuccess.Should().BeTrue();
        result.Role.Should().Be(ApiKeyRole.Admin);
    }

    [Fact]
    public async Task ValidateAsync_WrongKeySharingLegacyShortPrefix_ReturnsInvalid()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        const string storedSecret = "sk-33pol-797a8b0b67b157d4d26dd186e5cc2c84";
        const string attackerSecret = "sk-33pol-797zzzzzzzzzzzzzzzzzzzzzzzzzzzz";
        await SeedKeyAsync(db, tenantId, storedSecret, ApiKeyRole.Admin, storedPrefix: storedSecret[..12]);

        var sut = CreateValidator(db);
        var result = await sut.ValidateAsync(attackerSecret);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(ApiKeyValidationFailure.Invalid);
    }

    [Fact]
    public async Task ValidateAsync_RevokedKeyWithLegacyShortPrefix_ReturnsRevokedFailure()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        const string secret = "sk-33pol-legacy-revoked-key-0001";
        await SeedKeyAsync(db, tenantId, secret, ApiKeyRole.Inference, revoked: true, storedPrefix: secret[..12]);

        var sut = CreateValidator(db);
        var result = await sut.ValidateAsync(secret);

        result.Failure.Should().Be(ApiKeyValidationFailure.Revoked);
    }

    /// <summary>
    /// A key the gateway never issued is answered from the negative cache on the second try — no
    /// second database lookup — while a key sharing its prefix, and a key issued afterwards under a
    /// different value, are unaffected.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_UnknownKey_IsRememberedAsInvalidWithoutASecondLookup()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        await SeedKeyAsync(db, tenantId, "sk-33pol-real-key-0000001", ApiKeyRole.Inference);

        using var negative = new ApiKeyNegativeCache();
        var apiKeys = new CountingApiKeyRepository(new ApiKeyRepository(db));
        var sut = new ApiKeyValidator(
            apiKeys,
            new TenantRepository(db),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new GatewaySecurityOptions { KeyPepper = Pepper }),
            negative);

        const string placeholder = "lm-studio";
        (await sut.ValidateAsync(placeholder)).Failure.Should().Be(ApiKeyValidationFailure.Invalid);
        (await sut.ValidateAsync(placeholder)).Failure.Should().Be(ApiKeyValidationFailure.Invalid);
        apiKeys.Lookups.Should().Be(1, "the second presentation must be served from the negative cache");

        // Cached by hash, so a different key is never confused with the remembered one.
        (await sut.ValidateAsync("sk-33pol-real-key-0000001")).IsSuccess.Should().BeTrue();
        apiKeys.Lookups.Should().Be(2);
    }

    /// <summary>
    /// Expiry used to be checked only on the miss path, so a key expiring inside the cache window
    /// kept authenticating for up to CacheTtlMinutes after ExpiresAt. The positive entry's lifetime
    /// is now capped at the key's remaining lifetime.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_KeyExpiringInsideCacheWindow_StopsAuthenticatingAtExpiry()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        const string secret = "sk-33pol-soon-to-expire-key";
        await SeedKeyAsync(db, tenantId, secret, ApiKeyRole.Inference, expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(300));

        var sut = CreateValidator(db);
        (await sut.ValidateAsync(secret)).IsSuccess.Should().BeTrue();

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        (await sut.ValidateAsync(secret)).Failure.Should().Be(ApiKeyValidationFailure.Expired);
    }

    /// <summary>
    /// A permanently deleted key must fail as <c>Invalid</c>, not <c>Revoked</c>. The difference is
    /// load-bearing: <c>IsRecognizedCredential</c> decides whether an unusable key must fail loudly on
    /// anonymous-capable routes, and only <c>Invalid</c> reaches the negative cache. Reporting a key the
    /// gateway no longer holds as "recognised" would also confirm it once existed.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_DeletedKey_IsInvalidRatherThanRevoked()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var secret = "sk-33pol-deleted-key";
        var keyId = Guid.NewGuid();
        var keys = new ApiKeyRepository(db);
        await keys.CreateAsync(new ApiKeyRecord(
            keyId,
            tenantId,
            ApiKeyHashing.Hash(secret, Pepper),
            ApiKeyHashing.CreatePrefix(secret),
            ApiKeyRole.Inference,
            [],
            null,
            RevokedAt: DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null));

        // While the row is there, its holder is told the credential was withdrawn.
        (await CreateValidator(db).ValidateAsync(secret)).Failure
            .Should().Be(ApiKeyValidationFailure.Revoked);

        await keys.DeleteAsync(keyId);

        var failure = (await CreateValidator(db).ValidateAsync(secret)).Failure;
        failure.Should().Be(ApiKeyValidationFailure.Invalid);
        failure!.Value.IsRecognizedCredential().Should().BeFalse();
    }

    /// <summary>
    /// Archiving requires a prior revoke, so this can only fail if that coupling is broken elsewhere —
    /// which is exactly why the validator does not take it on trust.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_ArchivedKey_IsRejected()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var secret = "sk-33pol-archived-key";
        db.ApiKeys.Add(new Pol33.Persistence.Entities.ApiKeyEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KeyHash = ApiKeyHashing.Hash(secret, Pepper),
            KeyPrefix = ApiKeyHashing.CreatePrefix(secret),
            Role = ApiKeyRole.Inference,
            Scopes = [],
            CreatedAt = DateTimeOffset.UtcNow,
            // Deliberately archived without RevokedAt, the state the service refuses to create.
            ArchivedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var sut = CreateValidator(db);

        (await sut.ValidateAsync(secret)).Failure.Should().Be(ApiKeyValidationFailure.Revoked);
    }

    private sealed class CountingApiKeyRepository(IApiKeyRepository inner) : IApiKeyRepository
    {
        public int Lookups { get; private set; }

        public Task<IReadOnlyList<ApiKeyRecord>> FindByPrefixesAsync(
            IReadOnlyCollection<string> keyPrefixes,
            CancellationToken cancellationToken = default)
        {
            Lookups++;
            return inner.FindByPrefixesAsync(keyPrefixes, cancellationToken);
        }

        public Task<ApiKeyRecord?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) =>
            inner.FindByPrefixAsync(keyPrefix, cancellationToken);

        public Task<ApiKeyRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<ApiKeyRecord>> GetByIdsAsync(
            IReadOnlyCollection<Guid> ids,
            CancellationToken cancellationToken = default) =>
            inner.GetByIdsAsync(ids, cancellationToken);

        public Task<IReadOnlyList<ApiKeyRecord>> ListByTenantAsync(
            Guid tenantId,
            bool includeArchived = false,
            CancellationToken cancellationToken = default) =>
            inner.ListByTenantAsync(tenantId, includeArchived, cancellationToken);

        public Task<ApiKeyRecord> CreateAsync(ApiKeyRecord apiKey, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(apiKey, cancellationToken);

        public Task RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default) =>
            inner.RevokeAsync(id, revokedAt, cancellationToken);

        public Task<ApiKeyRecord> UpdateMetadataAsync(Guid id, ApiKeyMetadataUpdate update, CancellationToken cancellationToken = default) =>
            inner.UpdateMetadataAsync(id, update, cancellationToken);

        public Task TouchLastUsedAsync(Guid id, DateTimeOffset atUtc, CancellationToken cancellationToken = default) =>
            inner.TouchLastUsedAsync(id, atUtc, cancellationToken);
    }

    private static ApiKeyValidator CreateValidator(Pol33.Persistence.GatewayDbContext db) =>
        new(
            new ApiKeyRepository(db),
            new TenantRepository(db),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new GatewaySecurityOptions { KeyPepper = Pepper }),
            new ApiKeyNegativeCache());

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
        string? keyCostCenter = null,
        string? storedPrefix = null)
    {
        var keyId = Guid.NewGuid();
        db.ApiKeys.Add(new Pol33.Persistence.Entities.ApiKeyEntity
        {
            Id = keyId,
            TenantId = tenantId,
            KeyHash = ApiKeyHashing.Hash(secret, Pepper),
            KeyPrefix = storedPrefix ?? ApiKeyHashing.CreatePrefix(secret),
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
