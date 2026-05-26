using Pol33.Core.Billing;

namespace Pol33.Core.Abstractions;

public interface IRateCardCostCalculator
{
    BillingCostBreakdown Calculate(RateCardRecord rateCard, long promptTokens, long completionTokens);
}
