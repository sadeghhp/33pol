using Microsoft.Extensions.Options;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Webhooks;

public sealed class DailyUsageWebhookPublisher(
    IDailyUsageRollupRepository rollups,
    IBillingWebhookDispatcher webhooks,
    IOptions<BillingOptions> options,
    BillingDailyUsageWebhookTracker dailyTracker)
{
    public async Task DispatchYesterdayAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (utcNow.Hour != options.Value.DailyWebhookUtcHour)
        {
            return;
        }

        var yesterday = DateOnly.FromDateTime(utcNow).AddDays(-1);
        var records = await rollups
            .GetRollupsAsync(yesterday, yesterday, tenantId: null, cancellationToken)
            .ConfigureAwait(false);

        foreach (var group in records.Where(r => r.TenantId is not null).GroupBy(r => r.TenantId!.Value))
        {
            if (!dailyTracker.TryMarkSent(group.Key, yesterday))
            {
                continue;
            }

            var dayRollups = group.ToList();
            var tenantId = group.Key;

            // Release the once-per-tenant-day reservation if delivery never succeeds, so the next
            // scheduled pass can retry rather than the summary being lost outright.
            await webhooks.DispatchAsync(
                "usage.daily",
                new
                {
                    tenantId,
                    usageDate = yesterday.ToString("O"),
                    promptTokens = dayRollups.Sum(r => r.PromptTokens),
                    completionTokens = dayRollups.Sum(r => r.CompletionTokens),
                    totalCost = dayRollups.Sum(r => r.TotalCost),
                    requestCount = dayRollups.Sum(r => r.RequestCount),
                    currency = options.Value.DefaultCurrency,
                },
                onPermanentFailure: () => dailyTracker.Release(tenantId, yesterday),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
