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
    private readonly IApiKeyLifecycleEventRepository _lifecycle;
    private readonly IGatewayErrorArchive? _gatewayErrors;
    private readonly GatewaySecurityOptions _securityOptions;

    public AdminKeyService(
        IApiKeyRepository apiKeys,
        IApiKeyValidator validator,
        IBillingEventRepository billingEvents,
        IApiKeyLifecycleEventRepository lifecycle,
        IOptions<GatewaySecurityOptions> securityOptions,
        IGatewayErrorArchive? gatewayErrors = null)
    {
        _apiKeys = apiKeys;
        _validator = validator;
        _billingEvents = billingEvents;
        _lifecycle = lifecycle;
        // Optional: deployments without a configured connection string have no error archive, and a
        // missing one must not make key management unavailable. Absent, the usage check simply falls
        // back to its two stronger signals.
        _gatewayErrors = gatewayErrors;
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

        if (request.ExpiresAt is { } requestedExpiry && requestedExpiry <= now)
        {
            throw new ArgumentException("expiresAt must be in the future.", nameof(request));
        }

        var record = await _apiKeys.CreateAsync(
            new ApiKeyRecord(
                Guid.NewGuid(),
                tenantId,
                hash,
                prefix,
                request.Role,
                request.Scopes,
                ExpiresAt: request.ExpiresAt,
                RevokedAt: null,
                now,
                LastUsedAt: null,
                NormalizeOptional(request.Label),
                NormalizeOptional(request.Assignee),
                NormalizeOptional(request.Description),
                NormalizeOptional(request.CostCenter)),
            cancellationToken).ConfigureAwait(false);

        await _lifecycle.AppendAsync(
            NewEvent(record, ApiKeyLifecycleEvent.Created, now, actorKeyId: null),
            cancellationToken).ConfigureAwait(false);

        return ToCreatedResponse(record, secret);
    }

    public async Task<IReadOnlyList<AdminApiKeyListItem>> ListAsync(
        Guid tenantId,
        bool includeUsageSummary = false,
        bool includeArchived = false,
        Guid? actorKeyId = null,
        CancellationToken cancellationToken = default)
    {
        var records = await _apiKeys
            .ListByTenantAsync(tenantId, includeArchived, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyDictionary<Guid, ApiKeyUsageSummary>? summaries = null;
        if (includeUsageSummary && records.Count > 0)
        {
            var (fromDate, toDate) = GetMonthToDateRange();
            summaries = await _billingEvents
                .GetUsageSummariesAsync(tenantId, fromDate, toDate, cancellationToken)
                .ConfigureAwait(false);
        }

        // A LastUsedAt already settles the question; everything else has to be looked up. The probe is
        // batched — one query for the whole set rather than one per key — so a tenant with a few
        // hundred keys does not turn this listing into hundreds of round trips.
        //
        // Note this covers live keys too, not just the revoked ones whose CanDelete depends on it:
        // HasUsage is a published field claiming to report any billing or error record, and narrowing
        // the probe to deletion candidates would quietly make it lie about every other key.
        var undecided = records
            .Where(r => r.LastUsedAt is null)
            .Select(r => r.Id)
            .ToArray();
        IReadOnlySet<Guid> usedIds = undecided.Length == 0
            ? new HashSet<Guid>()
            : await FindKeysWithRecordedUsageAsync(undecided, cancellationToken).ConfigureAwait(false);

        return records
            .Select(record =>
            {
                var hasUsage = record.LastUsedAt is not null || usedIds.Contains(record.Id);
                return ToListItem(
                    record,
                    summaries is not null && summaries.TryGetValue(record.Id, out var summary) ? summary : null,
                    hasUsage,
                    canDelete: record.RevokedAt is not null && !hasUsage && record.Id != actorKeyId);
            })
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

        if (request.UpdateExpiry &&
            request.ExpiresAt is { } requestedExpiry &&
            requestedExpiry <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("expiresAt must be in the future.", nameof(request));
        }

        var updated = await _apiKeys.UpdateMetadataAsync(
            keyId,
            new ApiKeyMetadataUpdate(
                request.Label,
                request.Assignee,
                request.Description,
                request.CostCenter,
                request.ExpiresAt,
                request.UpdateExpiry),
            cancellationToken).ConfigureAwait(false);

        _validator.InvalidateCache(keyId);

        // Same DTO the listing returns, so it has to carry the same truth. CanDelete stays false by
        // construction: this method refuses revoked keys above, and deletion requires a revoked key.
        var hasUsage = await HasUsageHistoryAsync(updated, cancellationToken).ConfigureAwait(false);
        return ToListItem(updated, hasUsage: hasUsage);
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

    public async Task RevokeAsync(
        Guid tenantId,
        Guid keyId,
        Guid? actorKeyId = null,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireTenantKeyAsync(tenantId, keyId, cancellationToken).ConfigureAwait(false);

        if (record.RevokedAt is not null)
        {
            return;
        }

        await GuardTransitionAsync(record, actorKeyId, "revoke", cancellationToken).ConfigureAwait(false);
        await RevokeCoreAsync(record, actorKeyId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RevokeManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> keyIds,
        Guid? actorKeyId = null,
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

            // Batch revoke skips what it may not do rather than failing the whole batch: the caller
            // asked to revoke a set, and one protected key in it should not strand the other
            // nineteen. The response's count is what tells them how many actually went.
            if (record.Id == actorKeyId)
            {
                continue;
            }

            if (IsAdminRole(record.Role) &&
                await IsLastActiveAdminKeyAsync(tenantId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                await RevokeCoreAsync(record, actorKeyId, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiKeyLifecycleException)
            {
                // The pre-flight check above already skips the sequential case; this catches the
                // concurrent one, where RevokeCoreAsync's post-write recheck finds the key was the
                // last admin after all and reverts it. Skip, as with every other protected key —
                // one contended key must not cost the caller the rest of the batch.
                continue;
            }

            revokedCount++;
        }

        return revokedCount;
    }

    public async Task ArchiveAsync(
        Guid tenantId,
        Guid keyId,
        Guid? actorKeyId = null,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireTenantKeyAsync(tenantId, keyId, cancellationToken).ConfigureAwait(false);

        if (record.ArchivedAt is not null)
        {
            throw ApiKeyLifecycleException.AlreadyArchived();
        }

        // Archiving a live credential would hide a key that still authenticates, which is exactly the
        // key an operator most needs to see. Revocation first is what makes archiving safe.
        if (record.RevokedAt is null)
        {
            throw ApiKeyLifecycleException.NotRevoked("archived");
        }

        var now = DateTimeOffset.UtcNow;
        var hadUsage = await HasUsageHistoryAsync(record, cancellationToken).ConfigureAwait(false);

        await _apiKeys.ArchiveAsync(keyId, now, cancellationToken).ConfigureAwait(false);
        await _lifecycle.AppendAsync(
            NewEvent(record, ApiKeyLifecycleEvent.Archived, now, actorKeyId, hadUsage),
            cancellationToken).ConfigureAwait(false);

        // Harmless today (the key is already revoked, so nothing positive can be cached) and correct
        // if archiving is ever decoupled from revocation.
        _validator.InvalidateCache(keyId);
    }

    public async Task UnarchiveAsync(
        Guid tenantId,
        Guid keyId,
        Guid? actorKeyId = null,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireTenantKeyAsync(tenantId, keyId, cancellationToken).ConfigureAwait(false);

        if (record.ArchivedAt is null)
        {
            throw ApiKeyLifecycleException.NotArchived();
        }

        var now = DateTimeOffset.UtcNow;
        var hadUsage = await HasUsageHistoryAsync(record, cancellationToken).ConfigureAwait(false);

        await _apiKeys.UnarchiveAsync(keyId, cancellationToken).ConfigureAwait(false);
        await _lifecycle.AppendAsync(
            NewEvent(record, ApiKeyLifecycleEvent.Unarchived, now, actorKeyId, hadUsage),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdminApiKeyListItem> DeleteAsync(
        Guid tenantId,
        Guid keyId,
        Guid? actorKeyId,
        string? confirmKeyPrefix,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireTenantKeyAsync(tenantId, keyId, cancellationToken).ConfigureAwait(false);

        if (record.Id == actorKeyId)
        {
            throw ApiKeyLifecycleException.SelfAction("delete");
        }

        // Revocation first is not bureaucracy: it closes the window between reading the key list and
        // clicking delete, in which the key could serve its first request. Once RevokedAt is set the
        // validator rejects the credential, so no new usage can appear after the check below.
        if (record.RevokedAt is null)
        {
            throw ApiKeyLifecycleException.NotRevoked("deleted");
        }

        if (!string.Equals(confirmKeyPrefix?.Trim(), record.KeyPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "confirmKeyPrefix must match the key's prefix.",
                nameof(confirmKeyPrefix));
        }

        if (await HasUsageHistoryAsync(record, cancellationToken).ConfigureAwait(false))
        {
            var billingEventCount = await _billingEvents
                .CountEventsForKeyAsync(record.Id, cancellationToken)
                .ConfigureAwait(false);
            throw ApiKeyLifecycleException.HasUsage(billingEventCount, record.LastUsedAt);
        }

        // Tombstone first. If the delete then fails we are left with an extra history row, which is
        // strictly better than a vanished key with no record of who removed it.
        await _lifecycle.AppendAsync(
            NewEvent(record, ApiKeyLifecycleEvent.Deleted, DateTimeOffset.UtcNow, actorKeyId),
            cancellationToken).ConfigureAwait(false);

        await _apiKeys.DeleteAsync(keyId, cancellationToken).ConfigureAwait(false);
        _validator.InvalidateCache(keyId);

        return ToListItem(record);
    }

    public async Task<AdminApiKeyLifecycleResponse> GetLifecycleAsync(
        Guid tenantId,
        Guid keyId,
        CancellationToken cancellationToken = default)
    {
        var record = await _apiKeys.GetByIdAsync(keyId, cancellationToken).ConfigureAwait(false);
        if (record is not null && record.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException("API key does not belong to the current tenant.");
        }

        // Scoped by (tenant, key) rather than by the key row, because the interesting case is the one
        // where the row is gone.
        var events = await _lifecycle
            .ListForKeyAsync(tenantId, keyId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null && events.Count == 0)
        {
            throw new KeyNotFoundException($"API key '{keyId}' was not found.");
        }

        var latest = events.Count > 0 ? events[^1] : null;

        return new AdminApiKeyLifecycleResponse
        {
            Id = keyId,
            KeyPrefix = record?.KeyPrefix ?? latest?.KeyPrefix ?? string.Empty,
            Label = record?.Label ?? latest?.Label,
            Status = DescribeStatus(record),
            Exists = record is not null,
            Events = events.Select(ToLifecycleEntry).ToList(),
        };
    }

    private async Task RevokeCoreAsync(
        ApiKeyRecord record,
        Guid? actorKeyId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var hadUsage = await HasUsageHistoryAsync(record, cancellationToken).ConfigureAwait(false);

        // State first, history second — the reverse of DeleteAsync, and deliberately so. Here the key
        // row survives and carries RevokedAt itself, so a failure between the two loses an audit line
        // but nothing about the key's state. In DeleteAsync the row is what disappears, which is why
        // the tombstone has to be written before the thing it describes is gone.
        await _apiKeys.RevokeAsync(record.Id, now, cancellationToken).ConfigureAwait(false);

        // The pre-flight count in GuardTransitionAsync is a read on one connection and this write is
        // another, so two admins revoking the tenant's last two admin keys at the same moment can
        // both see a count of two and both pass. Re-reading after the write is what actually decides
        // it: whoever now sees zero puts their key back. Under a genuine race both revert and both
        // are told to try again, which costs a retry and never costs the tenant its way in.
        if (IsAdminRole(record.Role) &&
            await _apiKeys.CountActiveAdminKeysAsync(record.TenantId, cancellationToken).ConfigureAwait(false) == 0)
        {
            await _apiKeys.RestoreRevokedAsync(record.Id, cancellationToken).ConfigureAwait(false);
            _validator.InvalidateCache(record.Id);
            throw ApiKeyLifecycleException.LastAdminKey();
        }

        await _lifecycle.AppendAsync(
            NewEvent(record, ApiKeyLifecycleEvent.Revoked, now, actorKeyId, hadUsage),
            cancellationToken).ConfigureAwait(false);

        _validator.InvalidateCache(record.Id);
    }

    /// <summary>
    /// Rejects the two revocations an admin cannot undo: the key they are authenticating with, and
    /// the tenant's last way into its own control plane.
    /// </summary>
    private async Task GuardTransitionAsync(
        ApiKeyRecord record,
        Guid? actorKeyId,
        string action,
        CancellationToken cancellationToken)
    {
        if (record.Id == actorKeyId)
        {
            throw ApiKeyLifecycleException.SelfAction(action);
        }

        if (IsAdminRole(record.Role) &&
            await IsLastActiveAdminKeyAsync(record.TenantId, cancellationToken).ConfigureAwait(false))
        {
            throw ApiKeyLifecycleException.LastAdminKey();
        }
    }

    private async Task<bool> IsLastActiveAdminKeyAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await _apiKeys.CountActiveAdminKeysAsync(tenantId, cancellationToken).ConfigureAwait(false) <= 1;

    private static bool IsAdminRole(ApiKeyRole role) => role is ApiKeyRole.Admin or ApiKeyRole.Both;

    /// <summary>
    /// Whether the key has ever been used. Three signals, OR-ed, in descending order of how often
    /// they settle it: <c>LastUsedAt</c> is written on the first successful authentication, billing
    /// events are the authoritative ledger but are written asynchronously and may lag behind it, and
    /// a gateway error record proves a request reached the pipeline even if it then failed.
    /// </summary>
    /// <remarks>
    /// The error signal is deliberately the weakest of the three and mostly redundant: a gateway
    /// error carries an api key id only when the request authenticated, and authenticating is what
    /// sets <c>LastUsedAt</c>. It covers the narrow window where the touch has not landed yet. It
    /// stays because the cost of a false "never used" here is an irreversible deletion.
    /// </remarks>
    private async Task<bool> HasUsageHistoryAsync(ApiKeyRecord key, CancellationToken cancellationToken)
    {
        if (key.LastUsedAt is not null)
        {
            return true;
        }

        if (await _billingEvents.HasEventsForKeyAsync(key.Id, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (_gatewayErrors is null)
        {
            return false;
        }

        return await _gatewayErrors
            .HasEventsForKeyAsync(key.Id.ToString(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The batched counterpart of <see cref="HasUsageHistoryAsync"/>: the subset of
    /// <paramref name="apiKeyIds"/> that any ledger or error record names. Must stay in step with the
    /// single-key form, or the console would offer a delete the endpoint then refuses.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> FindKeysWithRecordedUsageAsync(
        IReadOnlyCollection<Guid> apiKeyIds,
        CancellationToken cancellationToken)
    {
        var used = new HashSet<Guid>(
            await _billingEvents.FindKeysWithEventsAsync(apiKeyIds, cancellationToken).ConfigureAwait(false));

        if (_gatewayErrors is null)
        {
            return used;
        }

        var remaining = apiKeyIds.Where(id => !used.Contains(id)).ToArray();
        if (remaining.Length == 0)
        {
            return used;
        }

        var byErrors = await _gatewayErrors
            .FindKeysWithEventsAsync(remaining.Select(id => id.ToString()).ToArray(), cancellationToken)
            .ConfigureAwait(false);

        foreach (var id in remaining)
        {
            if (byErrors.Contains(id.ToString()))
            {
                used.Add(id);
            }
        }

        return used;
    }

    private static ApiKeyLifecycleEventRecord NewEvent(
        ApiKeyRecord record,
        ApiKeyLifecycleEvent lifecycleEvent,
        DateTimeOffset occurredAt,
        Guid? actorKeyId,
        bool hadUsage = false) =>
        new(
            Guid.NewGuid(),
            record.Id,
            record.TenantId,
            record.KeyPrefix,
            lifecycleEvent,
            occurredAt,
            record.Label,
            actorKeyId,
            hadUsage);

    private static AdminApiKeyLifecycleEntry ToLifecycleEntry(ApiKeyLifecycleEventRecord record) =>
        new()
        {
            Id = record.Id,
            Event = record.Event.ToString(),
            OccurredAt = record.OccurredAt,
            KeyPrefix = record.KeyPrefix,
            Label = record.Label,
            ActorApiKeyId = record.ActorApiKeyId,
            HadUsage = record.HadUsage,
        };

    private static string DescribeStatus(ApiKeyRecord? record) =>
        record is null
            ? ApiKeyStatus.Deleted
            : ApiKeyStatus.Describe(
                record.ArchivedAt is not null,
                record.RevokedAt is not null,
                record.ExpiresAt);

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

    private static AdminApiKeyListItem ToListItem(
        ApiKeyRecord record,
        ApiKeyUsageSummary? usageSummary = null,
        bool hasUsage = false,
        bool canDelete = false) =>
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
            ArchivedAt = record.ArchivedAt,
            UsageSummary = usageSummary,
            HasUsage = hasUsage,
            CanDelete = canDelete,
        };

    private static AdminApiKeyCreatedResponse ToCreatedResponse(ApiKeyRecord record, string secret) =>
        new()
        {
            Id = record.Id,
            Secret = secret,
            KeyPrefix = record.KeyPrefix,
            Role = record.Role,
            CreatedAt = record.CreatedAt,
            ExpiresAt = record.ExpiresAt,
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
