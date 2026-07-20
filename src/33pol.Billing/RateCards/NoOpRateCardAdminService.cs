using Pol33.Core.Abstractions;
using Pol33.Core.Billing;

namespace Pol33.Billing.RateCards;

/// <summary>
/// Registered when no database is configured. Model pricing lives in the rate_cards table, so
/// there is nowhere to persist it; reads report "unpriced" and writes fail the same way model
/// registry writes do (see ModelRegistryWriter).
/// </summary>
public sealed class NoOpRateCardAdminService : IRateCardAdminService
{
    private const string Unavailable = "Model pricing requires a configured database.";

    public Task<ModelPricingUpdateResult> SetPricingAsync(
        string modelId,
        ModelPricing pricing,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ModelPricingUpdateResult.Fail(Unavailable, 503));

    public Task<ModelPricingUpdateResult> ClearPricingAsync(
        string modelId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ModelPricingUpdateResult.Fail(Unavailable, 503));

    public Task<IReadOnlyDictionary<string, ModelPricing>> GetPricingByModelAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ModelPricing>>(
            new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase));
}
