using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;

namespace Pol33.App;

/// <summary>
/// Validates <see cref="BillingOptions"/>: the self-contained range checks in
/// <see cref="BillingOptionsValidation"/>, plus the reservation TTL against the resilience timings
/// it depends on. Budget reservations and request deadlines are configured in separate sections
/// but are only correct relative to one another, so that check has to span both.
/// </summary>
internal sealed class BillingOptionsValidateOptions(IOptions<GatewayOptions> gatewayOptions)
    : IValidateOptions<BillingOptions>
{
    public ValidateOptionsResult Validate(string? name, BillingOptions options)
    {
        var errors = new List<string>(BillingOptionsValidation.Validate(options));

        if (options.BudgetReservationTtlSeconds >= 1 &&
            !BillingReservationTtlPolicy.IsSufficient(gatewayOptions.Value.Resilience, options))
        {
            errors.Add(BillingReservationTtlPolicy.DescribeInsufficient(gatewayOptions.Value.Resilience, options));
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
