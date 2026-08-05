using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;

namespace Pol33.App;

/// <summary>
/// Validates <see cref="BillingOptions"/> against the resilience timings it depends on. Budget
/// reservations and request deadlines are configured in separate sections but are only correct
/// relative to one another, so the check has to span both.
/// </summary>
internal sealed class BillingOptionsValidateOptions(IOptions<GatewayOptions> gatewayOptions)
    : IValidateOptions<BillingOptions>
{
    public ValidateOptionsResult Validate(string? name, BillingOptions options)
    {
        var errors = new List<string>();

        if (options.UsageWriterBatchSize < 1)
        {
            errors.Add($"{BillingOptions.SectionName}.{nameof(BillingOptions.UsageWriterBatchSize)} must be at least 1.");
        }

        if (options.UsageWriterFlushIntervalMs < 1)
        {
            errors.Add($"{BillingOptions.SectionName}.{nameof(BillingOptions.UsageWriterFlushIntervalMs)} must be at least 1 ms.");
        }

        if (options.BudgetReservationTtlSeconds < 1)
        {
            errors.Add($"{BillingOptions.SectionName}.{nameof(BillingOptions.BudgetReservationTtlSeconds)} must be at least 1 second.");
        }
        else if (!BillingReservationTtlPolicy.IsSufficient(gatewayOptions.Value.Resilience, options))
        {
            errors.Add(BillingReservationTtlPolicy.DescribeInsufficient(gatewayOptions.Value.Resilience, options));
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
