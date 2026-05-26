using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Webhooks;

public static class DailyUsageWebhookDispatch
{
    public static bool ShouldRunAtUtcHour(int currentUtcHour, int configuredUtcHour) =>
        currentUtcHour == configuredUtcHour;

    public static async Task DispatchYesterdayAsync(
        IDailyUsageRollupRepository rollups,
        IBillingWebhookDispatcher webhooks,
        BillingDailyUsageWebhookTracker tracker,
        BillingOptions options,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldRunAtUtcHour(utcNow.Hour, options.DailyWebhookUtcHour))
        {
            return;
        }

        var yesterday = DateOnly.FromDateTime(utcNow).AddDays(-1);
        var records = await rollups
            .GetRollupsAsync(yesterday, yesterday, tenantId: null, cancellationToken)
            .ConfigureAwait(false);

        foreach (var group in records.Where(r => r.TenantId is not null).GroupBy(r => r.TenantId!.Value))
        {
            if (!tracker.TryMarkSent(group.Key, yesterday))
            {
                continue;
            }

            var dayRollups = group.ToList();
            await webhooks.DispatchAsync(
                "usage.daily",
                new
                {
                    tenantId = group.Key,
                    usageDate = yesterday.ToString("O"),
                    promptTokens = dayRollups.Sum(r => r.PromptTokens),
                    completionTokens = dayRollups.Sum(r => r.CompletionTokens),
                    totalCost = dayRollups.Sum(r => r.TotalCost),
                    requestCount = dayRollups.Sum(r => r.RequestCount),
                    currency = options.DefaultCurrency,
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}
