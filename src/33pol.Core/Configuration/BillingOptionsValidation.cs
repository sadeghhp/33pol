namespace Pol33.Core.Configuration;

/// <summary>
/// Range checks for <see cref="BillingOptions"/> that hold on their own (the reservation-TTL
/// check also needs the resilience timings and lives with the host's validator).
/// </summary>
/// <remarks>
/// These options were bound without validation, and several bad values fail silently rather than
/// loudly: a non-positive flush interval throws inside the usage writer's timer loop, which nobody
/// observed until shutdown, so no periodic flush ever ran; a <c>DailyWebhookUtcHour</c> outside
/// 0–23 can never equal the current hour, so the daily summary silently never fires; a poll
/// interval above an hour can skip the target hour entirely.
/// </remarks>
public static class BillingOptionsValidation
{
    public static IReadOnlyList<string> Validate(BillingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (options.UsageWriterBatchSize < 1)
        {
            errors.Add(Error(nameof(BillingOptions.UsageWriterBatchSize), "must be at least 1."));
        }

        if (options.UsageWriterFlushIntervalMs < 1)
        {
            errors.Add(Error(nameof(BillingOptions.UsageWriterFlushIntervalMs), "must be at least 1 ms."));
        }

        if (options.UsageWriterMaxFlushRetries < 0)
        {
            errors.Add(Error(nameof(BillingOptions.UsageWriterMaxFlushRetries), "must be 0 or greater."));
        }

        if (options.UsageWriterMaxPendingEvents < 1)
        {
            errors.Add(Error(nameof(BillingOptions.UsageWriterMaxPendingEvents), "must be at least 1."));
        }

        if (options.DailyWebhookUtcHour is < 0 or > 23)
        {
            errors.Add(Error(nameof(BillingOptions.DailyWebhookUtcHour), "must be between 0 and 23 (UTC hour)."));
        }

        if (options.DailyWebhookPollIntervalSeconds is < 1 or > 3600)
        {
            errors.Add(Error(
                nameof(BillingOptions.DailyWebhookPollIntervalSeconds),
                "must be between 1 and 3600 seconds; a longer interval can skip the scheduled hour entirely."));
        }

        if (options.UsageRetentionDays < 1)
        {
            errors.Add(Error(nameof(BillingOptions.UsageRetentionDays), "must be at least 1 day."));
        }

        if (options.ReconciliationIntervalMinutes < 1)
        {
            errors.Add(Error(nameof(BillingOptions.ReconciliationIntervalMinutes), "must be at least 1 minute."));
        }

        if (options.ReconciliationLookbackDays < 1)
        {
            errors.Add(Error(nameof(BillingOptions.ReconciliationLookbackDays), "must be at least 1 day."));
        }

        if (options.RateCardCacheTtlSeconds < 1)
        {
            errors.Add(Error(nameof(BillingOptions.RateCardCacheTtlSeconds), "must be at least 1 second."));
        }

        if (options.BudgetCacheTtlSeconds < 1)
        {
            errors.Add(Error(nameof(BillingOptions.BudgetCacheTtlSeconds), "must be at least 1 second."));
        }

        if (options.BudgetSpendCacheTtlSeconds < 1)
        {
            errors.Add(Error(nameof(BillingOptions.BudgetSpendCacheTtlSeconds), "must be at least 1 second."));
        }

        if (options.BudgetReservationDefaultMaxTokens < 1)
        {
            errors.Add(Error(nameof(BillingOptions.BudgetReservationDefaultMaxTokens), "must be at least 1."));
        }

        if (options.BudgetReservationTtlSeconds < 1)
        {
            errors.Add(Error(nameof(BillingOptions.BudgetReservationTtlSeconds), "must be at least 1 second."));
        }

        if (options.DefaultWarningThresholdRatio is <= 0m or > 1m)
        {
            errors.Add(Error(nameof(BillingOptions.DefaultWarningThresholdRatio), "must be greater than 0 and at most 1."));
        }

        if (options.BudgetWarningTrackerRetentionLimit < 1)
        {
            errors.Add(Error(nameof(BillingOptions.BudgetWarningTrackerRetentionLimit), "must be at least 1."));
        }

        if (options.DailyWebhookTrackerRetentionLimit < 1)
        {
            errors.Add(Error(nameof(BillingOptions.DailyWebhookTrackerRetentionLimit), "must be at least 1."));
        }

        return errors;
    }

    private static string Error(string property, string message) =>
        $"{BillingOptions.SectionName}.{property} {message}";
}
