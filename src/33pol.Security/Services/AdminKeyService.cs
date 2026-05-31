using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Usage;
using Pol33.Persistence.Security;
using Pol33.Security.Configuration;

namespace Pol33.Security.Services;

public sealed class AdminKeyService : IAdminKeyService
{
    private readonly IApiKeyRepository _apiKeys;
    private readonly IApiKeyValidator _validator;
    private readonly IBillingEventRepository _billingEvents;
    private readonly GatewaySecurityOptions _securityOptions;

    public AdminKeyService(
        IApiKeyRepository apiKeys,
        IApiKeyValidator validator,
        IBillingEventRepository billingEvents,
        IOptions<GatewaySecurityOptions> securityOptions)
    {
        _apiKeys = apiKeys;
        _validator = validator;
        _billingEvents = billingEvents;
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
                LastUsedAt: null,
                NormalizeOptional(request.Label),
                NormalizeOptional(request.Assignee),
                NormalizeOptional(request.Description),
                NormalizeOptional(request.CostCenter)),
            cancellationToken).ConfigureAwait(false);

        return ToCreatedResponse(record, secret);
    }

    public async Task<IReadOnlyList<AdminApiKeyListItem>> ListAsync(
        Guid tenantId,
        bool includeUsageSummary = false,
        CancellationToken cancellationToken = default)
    {
        var records = await _apiKeys.ListByTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<Guid, ApiKeyUsageSummary>? summaries = null;
        if (includeUsageSummary && records.Count > 0)
        {
            var (fromDate, toDate) = GetMonthToDateRange();
            summaries = await _billingEvents
                .GetUsageSummariesAsync(tenantId, fromDate, toDate, cancellationToken)
                .ConfigureAwait(false);
        }

        return records
            .Select(record => ToListItem(
                record,
                record.Id is var id && summaries is not null && summaries.TryGetValue(id, out var summary)
                    ? summary
                    : null))
            .ToList();
    }

    public async Task<AdminApiKeyListItem> UpdateAsync(
        Guid tenantId,
        Guid keyId,
        UpdateAdminApiKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var record = await RequireTenantKeyAsync(tenantId, keyId, cancellationToken).ConfigureAwait(false);
        if (record.RevokedAt is not null)
        {
            throw new InvalidOperationException("Revoked API keys cannot be updated.");
        }

        var updated = await _apiKeys.UpdateMetadataAsync(
            keyId,
            new ApiKeyMetadataUpdate(
                request.Label,
                request.Assignee,
                request.Description,
                request.CostCenter),
            cancellationToken).ConfigureAwait(false);

        _validator.InvalidateCache(keyId);
        return ToListItem(updated);
    }

    public async Task<AdminApiKeyUsageResponse> GetUsageAsync(
        Guid tenantId,
        Guid keyId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireTenantKeyAsync(tenantId, keyId, cancellationToken).ConfigureAwait(false);
        var (from, to) = ResolveDateRange(fromDate, toDate);

        var summaries = await _billingEvents
            .GetUsageSummariesAsync(tenantId, from, to, cancellationToken)
            .ConfigureAwait(false);
        summaries.TryGetValue(keyId, out var summary);

        var events = await _billingEvents.QueryAsync(
            new BillingEventQuery(from, to, tenantId, ApiKeyId: keyId, Limit: 500),
            cancellationToken).ConfigureAwait(false);

        var enriched = events
            .Select(e => AdminBillingEventMapper.ToListItem(
                e,
                new Dictionary<Guid, AdminBillingEventMapper.ApiKeyLookup>
                {
                    [keyId] = new(record.KeyPrefix, record.Assignee),
                }))
            .ToList();

        return new AdminApiKeyUsageResponse
        {
            Id = record.Id,
            KeyPrefix = record.KeyPrefix,
            Label = record.Label,
            Assignee = record.Assignee,
            CostCenter = record.CostCenter,
            FromDate = from,
            ToDate = to,
            Summary = summary ?? new ApiKeyUsageSummary(),
            Events = enriched,
        };
    }

    public async Task RevokeAsync(Guid tenantId, Guid keyId, CancellationToken cancellationToken = default)
    {
        _ = await RequireTenantKeyAsync(tenantId, keyId, cancellationToken).ConfigureAwait(false);

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

    private async Task<ApiKeyRecord> RequireTenantKeyAsync(
        Guid tenantId,
        Guid keyId,
        CancellationToken cancellationToken)
    {
        var record = await _apiKeys.GetByIdAsync(keyId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"API key '{keyId}' was not found.");

        if (record.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException("API key does not belong to the current tenant.");
        }

        return record;
    }

    private static AdminApiKeyListItem ToListItem(ApiKeyRecord record, ApiKeyUsageSummary? usageSummary = null) =>
        new()
        {
            Id = record.Id,
            KeyPrefix = record.KeyPrefix,
            Role = record.Role,
            CreatedAt = record.CreatedAt,
            ExpiresAt = record.ExpiresAt,
            RevokedAt = record.RevokedAt,
            LastUsedAt = record.LastUsedAt,
            Label = record.Label,
            Assignee = record.Assignee,
            Description = record.Description,
            CostCenter = record.CostCenter,
            UsageSummary = usageSummary,
        };

    private static AdminApiKeyCreatedResponse ToCreatedResponse(ApiKeyRecord record, string secret) =>
        new()
        {
            Id = record.Id,
            Secret = secret,
            KeyPrefix = record.KeyPrefix,
            Role = record.Role,
            CreatedAt = record.CreatedAt,
            Label = record.Label,
            Assignee = record.Assignee,
            Description = record.Description,
            CostCenter = record.CostCenter,
        };

    private static (DateOnly From, DateOnly To) GetMonthToDateRange()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return (new DateOnly(today.Year, today.Month, 1), today);
    }

    private static (DateOnly From, DateOnly To) ResolveDateRange(DateOnly? fromDate, DateOnly? toDate)
    {
        if (fromDate is null && toDate is null)
        {
            return GetMonthToDateRange();
        }

        var to = toDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var from = fromDate ?? to;
        return from <= to ? (from, to) : (to, from);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string GenerateSecret() =>
        $"sk-33pol-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
}
