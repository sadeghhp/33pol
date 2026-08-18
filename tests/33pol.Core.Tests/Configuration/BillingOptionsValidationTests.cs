using Pol33.Core.Configuration;

namespace Pol33.Core.Tests.Configuration;

/// <summary>
/// BillingOptions used to be bound without range checks, and the failure modes were silent: a
/// non-positive flush interval killed the usage writer's timer loop unobserved, an out-of-range
/// webhook hour meant the daily summary never fired, and a poll interval above an hour could skip
/// the scheduled hour entirely.
/// </summary>
public sealed class BillingOptionsValidationTests
{
    [Fact]
    public void Validate_Defaults_ReturnNoErrors()
    {
        BillingOptionsValidation.Validate(new BillingOptions()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveFlushInterval_ReturnsError(int ms)
    {
        var errors = BillingOptionsValidation.Validate(new BillingOptions { UsageWriterFlushIntervalMs = ms });

        errors.Should().ContainSingle(e => e.Contains(nameof(BillingOptions.UsageWriterFlushIntervalMs), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NonPositiveBatchSize_ReturnsError()
    {
        var errors = BillingOptionsValidation.Validate(new BillingOptions { UsageWriterBatchSize = 0 });

        errors.Should().ContainSingle(e => e.Contains(nameof(BillingOptions.UsageWriterBatchSize), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    public void Validate_WebhookHourOutOfRange_ReturnsError(int hour)
    {
        var errors = BillingOptionsValidation.Validate(new BillingOptions { DailyWebhookUtcHour = hour });

        errors.Should().ContainSingle(e => e.Contains(nameof(BillingOptions.DailyWebhookUtcHour), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void Validate_WebhookPollIntervalOutOfRange_ReturnsError(int seconds)
    {
        var errors = BillingOptionsValidation.Validate(new BillingOptions { DailyWebhookPollIntervalSeconds = seconds });

        errors.Should().ContainSingle(e => e.Contains(nameof(BillingOptions.DailyWebhookPollIntervalSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NonPositiveCacheTtls_ReturnErrors()
    {
        var errors = BillingOptionsValidation.Validate(new BillingOptions
        {
            RateCardCacheTtlSeconds = 0,
            BudgetCacheTtlSeconds = 0,
            BudgetSpendCacheTtlSeconds = -5,
        });

        errors.Should().HaveCount(3);
        errors.Should().Contain(e => e.Contains(nameof(BillingOptions.RateCardCacheTtlSeconds), StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains(nameof(BillingOptions.BudgetCacheTtlSeconds), StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains(nameof(BillingOptions.BudgetSpendCacheTtlSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RetentionAndReconciliationWindows_MustBeAtLeastOneDay()
    {
        var errors = BillingOptionsValidation.Validate(new BillingOptions
        {
            UsageRetentionDays = 0,
            ReconciliationLookbackDays = 0,
            ReconciliationIntervalMinutes = 0,
        });

        errors.Should().HaveCount(3);
    }

    [Fact]
    public void Validate_UsageWriterRetryKnobs_AreRangeChecked()
    {
        var errors = BillingOptionsValidation.Validate(new BillingOptions
        {
            UsageWriterMaxFlushRetries = -1,
            UsageWriterMaxPendingEvents = 0,
        });

        errors.Should().HaveCount(2);
        errors.Should().Contain(e => e.Contains(nameof(BillingOptions.UsageWriterMaxFlushRetries), StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains(nameof(BillingOptions.UsageWriterMaxPendingEvents), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ErrorsNameTheBillingSection()
    {
        var errors = BillingOptionsValidation.Validate(new BillingOptions { DailyWebhookUtcHour = 99 });

        errors.Should().ContainSingle().Which.Should().StartWith(BillingOptions.SectionName + ".");
    }
}
