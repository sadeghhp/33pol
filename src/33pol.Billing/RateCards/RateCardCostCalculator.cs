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

    public BillingCostBreakdown CalculateFromTotalTokens(RateCardRecord rateCard, long totalTokens)
    {
        ArgumentNullException.ThrowIfNull(rateCard);

        if (totalTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalTokens), "Token counts cannot be negative.");
        }

        // Conservative: without the split, price everything at the dearer of the two rates rather
        // than assuming the cheaper (input) one, matching how budget reservation bounds unknown cost.
        var rate = Math.Max(rateCard.InputPricePerMillionTokens, rateCard.OutputPricePerMillionTokens);
        var cost = CalculateLineCost(totalTokens, rate);

        // Attributed to output, since output is normally the dearer side and the one that varies.
        return new BillingCostBreakdown(0m, cost, cost, rateCard.Currency);
    }

    /// <summary>
    /// Scale at which a single request's cost is stored.
    /// </summary>
    /// <remarks>
    /// Rounding each line to 6 places zeroed out small requests: at $0.15 per million tokens a
    /// handful of tokens costs less than 0.0000005, which rounded to exactly zero. Every such
    /// request was billed nothing, and the shortfall grew with request volume — the workload profile
    /// (many small calls) where it hurts most. Ten places keeps a single token of the cheapest
    /// realistic model representable while staying well inside the storage column's range.
    /// </remarks>
    internal const int CostScale = 10;

    internal static decimal CalculateLineCost(long tokens, decimal pricePerMillionTokens) =>
        tokens == 0 ? 0m : decimal.Round(tokens / TokensPerMillion * pricePerMillionTokens, CostScale);
}
