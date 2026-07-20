using Pol33.Core.Abstractions;
using Pol33.Core.Billing;

namespace Pol33.Billing.RateCards;

public sealed class RateCardAdminService(IRateCardRepository rateCards) : IRateCardAdminService
{
    /// <summary>
    /// The rate_cards price columns are decimal(18,6), leaving 12 digits ahead of the point.
    /// Values beyond this would be silently truncated by the provider, so reject them up front.
    /// </summary>
    private const decimal MaxPrice = 999_999_999_999.999999m;

    private const int PriceScale = 6;

    public async Task<ModelPricingUpdateResult> SetPricingAsync(
        string modelId,
        ModelPricing pricing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pricing);

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return ModelPricingUpdateResult.Fail("Model id is required to set pricing.", 400);
        }

        if (!TryNormalize(pricing.InputPricePerMillionTokens, "inputPricePerMillionTokens", out var input, out var error) ||
            !TryNormalize(pricing.OutputPricePerMillionTokens, "outputPricePerMillionTokens", out var output, out error))
        {
            return ModelPricingUpdateResult.Fail(error!, 400);
        }

        await rateCards
            .UpsertForModelAsync(modelId.Trim(), input, output, cancellationToken)
            .ConfigureAwait(false);

        return ModelPricingUpdateResult.Ok($"Pricing updated for model '{modelId}'.");
    }

    public async Task<ModelPricingUpdateResult> ClearPricingAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return ModelPricingUpdateResult.Fail("Model id is required to clear pricing.", 400);
        }

        await rateCards
            .DeleteForModelAsync(modelId.Trim(), cancellationToken)
            .ConfigureAwait(false);

        return ModelPricingUpdateResult.Ok($"Pricing cleared for model '{modelId}'.");
    }

    public async Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingByModelAsync(
        CancellationToken cancellationToken = default)
    {
        var active = await rateCards.GetActiveByModelAsync(cancellationToken).ConfigureAwait(false);

        return active.ToDictionary(
            static pair => pair.Key,
            static pair => new ModelPricing
            {
                InputPricePerMillionTokens = pair.Value.InputPricePerMillionTokens,
                OutputPricePerMillionTokens = pair.Value.OutputPricePerMillionTokens,
                Currency = pair.Value.Currency,
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryNormalize(decimal value, string field, out decimal normalized, out string? error)
    {
        normalized = 0m;

        if (value < 0m)
        {
            error = $"{field} cannot be negative.";
            return false;
        }

        if (value > MaxPrice)
        {
            error = $"{field} exceeds the maximum supported price of {MaxPrice}.";
            return false;
        }

        normalized = decimal.Round(value, PriceScale, MidpointRounding.AwayFromZero);
        error = null;
        return true;
    }
}
