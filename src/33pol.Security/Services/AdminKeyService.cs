using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Persistence.Security;
using Pol33.Security.Configuration;

namespace Pol33.Security.Services;

public sealed class AdminKeyService : IAdminKeyService
{
    private readonly IApiKeyRepository _apiKeys;
    private readonly IApiKeyValidator _validator;
    private readonly GatewaySecurityOptions _securityOptions;

    public AdminKeyService(
        IApiKeyRepository apiKeys,
        IApiKeyValidator validator,
        IOptions<GatewaySecurityOptions> securityOptions)
    {
        _apiKeys = apiKeys;
        _validator = validator;
        _securityOptions = securityOptions.Value;
    }

    public async Task<AdminApiKeyCreatedResponse> CreateAsync(
        Guid tenantId,
        CreateAdminApiKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        var secret = GenerateSecret();
        var prefix = ApiKeyHashing.CreatePrefix(secret);
        var hash = ApiKeyHashing.Hash(secret, _securityOptions.KeyPepper);
        var now = DateTimeOffset.UtcNow;

        var record = await _apiKeys.CreateAsync(
            new ApiKeyRecord(
                Guid.NewGuid(),
                tenantId,
                hash,
                prefix,
                request.Role,
                request.Scopes,
                ExpiresAt: null,
                RevokedAt: null,
                now,
                LastUsedAt: null),
            cancellationToken).ConfigureAwait(false);

        return new AdminApiKeyCreatedResponse
        {
            Id = record.Id,
            Secret = secret,
            KeyPrefix = record.KeyPrefix,
            Role = record.Role,
            CreatedAt = record.CreatedAt,
        };
    }

    public async Task<IReadOnlyList<AdminApiKeyListItem>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var records = await _apiKeys.ListByTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return records.Select(record => new AdminApiKeyListItem
        {
            Id = record.Id,
            KeyPrefix = record.KeyPrefix,
            Role = record.Role,
            CreatedAt = record.CreatedAt,
            ExpiresAt = record.ExpiresAt,
            RevokedAt = record.RevokedAt,
        }).ToList();
    }

    public async Task RevokeAsync(Guid tenantId, Guid keyId, CancellationToken cancellationToken = default)
    {
        var record = await _apiKeys.GetByIdAsync(keyId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"API key '{keyId}' was not found.");

        if (record.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException("API key does not belong to the current tenant.");
        }

        await _apiKeys.RevokeAsync(keyId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        _validator.InvalidateCache(keyId);
    }

    public async Task<int> RevokeManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> keyIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyIds);
        if (keyIds.Count == 0)
        {
            return 0;
        }

        var distinctIds = keyIds
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        var revokedCount = 0;
        foreach (var keyId in distinctIds)
        {
            var record = await _apiKeys.GetByIdAsync(keyId, cancellationToken).ConfigureAwait(false);
            if (record is null || record.TenantId != tenantId || record.RevokedAt is not null)
            {
                continue;
            }

            await _apiKeys.RevokeAsync(keyId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            _validator.InvalidateCache(keyId);
            revokedCount++;
        }

        return revokedCount;
    }

    internal static string GenerateSecret() =>
        $"sk-33pol-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
}
