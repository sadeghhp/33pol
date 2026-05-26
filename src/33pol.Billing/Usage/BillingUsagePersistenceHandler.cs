using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

public sealed class BillingUsagePersistenceHandler(
    IBillingEventRepository billingEvents,
    IDailyUsageRollupRepository rollups,
    IDailyUsageRollupAggregator aggregator) : IUsagePersistenceHandler
{
    public async ValueTask PersistAsync(UsageEvent usageEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usageEvent);

        var record = BillingEventMapper.FromUsageEvent(usageEvent);
        if (!await billingEvents.TryAppendAsync(record, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var usageDate = DateOnly.FromDateTime(record.RecordedAt.UtcDateTime);
        var existingRollups = await rollups
            .GetRollupsAsync(usageDate, usageDate, record.TenantId, cancellationToken)
            .ConfigureAwait(false);

        var existing = existingRollups.FirstOrDefault(r =>
            string.Equals(r.ModelId, record.ModelId, StringComparison.Ordinal) &&
            string.Equals(r.CostCenter, record.CostCenter, StringComparison.Ordinal));

        DailyUsageRollupRecord merged;
        if (existing is null)
        {
            merged = aggregator.Aggregate([record]).Single();
        }
        else
        {
            merged = existing with
            {
                PromptTokens = existing.PromptTokens + record.PromptTokens,
                CompletionTokens = existing.CompletionTokens + record.CompletionTokens,
                TotalCost = existing.TotalCost + (record.TotalCost ?? 0m),
                RequestCount = existing.RequestCount + 1,
            };
        }

        await rollups.UpsertRollupsAsync([merged], cancellationToken).ConfigureAwait(false);
    }
}
