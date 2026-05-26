using Pol33.Core.Abstractions;
using Pol33.Core.Billing;

namespace Pol33.Billing.RateCards;

public sealed class RateCardCostCalculator : IRateCardCostCalculator
{
    private const decimal TokensPerMillion = 1_000_000m;

    public BillingCostBreakdown Calculate(
        RateCardRecord rateCard,
        long promptTokens,
        long completionTokens)
    {
        ArgumentNullException.ThrowIfNull(rateCard);

        if (promptTokens < 0 || completionTokens < 0)
        {
            throw new ArgumentOutOfRangeException(
                promptTokens < 0 ? nameof(promptTokens) : nameof(completionTokens),
                "Token counts cannot be negative.");
        }

        var inputCost = CalculateLineCost(promptTokens, rateCard.InputPricePerMillionTokens);
        var outputCost = CalculateLineCost(completionTokens, rateCard.OutputPricePerMillionTokens);

        return new BillingCostBreakdown(
            inputCost,
            outputCost,
            inputCost + outputCost,
            rateCard.Currency);
    }

    internal static decimal CalculateLineCost(long tokens, decimal pricePerMillionTokens) =>
        tokens == 0 ? 0m : decimal.Round(tokens / TokensPerMillion * pricePerMillionTokens, 6);
}
