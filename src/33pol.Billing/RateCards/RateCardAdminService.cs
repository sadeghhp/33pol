using Pol33.Core.Abstractions;
using Pol33.Core.Billing;

namespace Pol33.Billing.RateCards;

public sealed class RateCardAdminService(
    IRateCardRepository rateCards,
    IModelRegistry registry) : IRateCardAdminService
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

        // Key pricing by exactly the id the routing layer uses. The registry resolves ids
        // case-insensitively and resolves aliases, so without this a price set as "GPT-4o" (or via
        // an alias) would never be found for the canonical "gpt-4o" and would silently never apply.
        if (!registry.TryGetModel(modelId.Trim(), out var model) || model is null)
        {
            return ModelPricingUpdateResult.Fail(
                $"Model '{modelId}' is not registered, so pricing cannot be set for it.",
                404);
        }

        await rateCards
            .UpsertForModelAsync(model.Id, input, output, cancellationToken)
            .ConfigureAwait(false);

        return ModelPricingUpdateResult.Ok($"Pricing updated for model '{model.Id}'.");
    }

    public async Task<ModelPricingUpdateResult> ClearPricingAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return ModelPricingUpdateResult.Fail("Model id is required to clear pricing.", 400);
        }

        // Deliberately does not consult the registry: this also runs after a model is deleted,
        // when the id is no longer registered. Case-insensitive matching is handled by the
        // NOCASE collation on model_id.
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
