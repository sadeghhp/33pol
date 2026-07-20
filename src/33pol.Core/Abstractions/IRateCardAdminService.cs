using Pol33.Core.Billing;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Admin-facing management of per-model pricing. Backed by rate cards in the database;
/// a no-op implementation is registered when no database is configured, so callers always
/// resolve and receive a 503 rather than failing to construct.
/// </summary>
public interface IRateCardAdminService
{
    Task<ModelPricingUpdateResult> SetPricingAsync(
        string modelId,
        ModelPricing pricing,
        CancellationToken cancellationToken = default);

    Task<ModelPricingUpdateResult> ClearPricingAsync(
        string modelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Current price for every priced model, keyed by model id. Empty when pricing is unavailable.
    /// </summary>
    Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingByModelAsync(
        CancellationToken cancellationToken = default);
}
