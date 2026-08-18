using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Core.Usage;

public static class AdminBillingEventMapper
{
    public static AdminBillingEventListItem ToListItem(
        BillingEventRecord record,
        IReadOnlyDictionary<Guid, ApiKeyLookup> keysById) =>
        new()
        {
            Id = record.Id,
            RequestId = record.RequestId,
            ApiKeyId = record.ApiKeyId,
            KeyPrefix = record.ApiKeyId is Guid keyId && keysById.TryGetValue(keyId, out var lookup)
                ? lookup.KeyPrefix
                : null,
            Assignee = record.ApiKeyId is Guid assigneeKeyId && keysById.TryGetValue(assigneeKeyId, out var assigneeLookup)
                ? assigneeLookup.Assignee
                : null,
            ModelId = record.ModelId,
            CostCenter = record.CostCenter,
            PromptTokens = record.PromptTokens,
            CompletionTokens = record.CompletionTokens,
            TotalCost = record.TotalCost,
            DurationMs = record.DurationMs,
            RecordedAt = record.RecordedAt,
        };

    public static async Task<IReadOnlyList<AdminBillingEventListItem>> EnrichAsync(
        IReadOnlyList<BillingEventRecord> events,
        IApiKeyRepository apiKeys,
        CancellationToken cancellationToken)
    {
        var keyIds = events
            .Select(e => e.ApiKeyId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        var keys = await apiKeys.GetByIdsAsync(keyIds, cancellationToken).ConfigureAwait(false);
        var keysById = keys.ToDictionary(k => k.Id, k => new ApiKeyLookup(k.KeyPrefix, k.Assignee));

        return events.Select(e => ToListItem(e, keysById)).ToList();
    }

    public sealed record ApiKeyLookup(string KeyPrefix, string? Assignee);
}
