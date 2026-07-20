using Pol33.Core.Billing;

namespace Pol33.Core.Abstractions;

public interface IRateCardRepository
{
    Task<RateCardRecord?> GetActiveForModelAsync(
        string modelId,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current active rate card for a model, or null when the model is unpriced.
    /// </summary>
    Task<RateCardRecord?> GetForModelAsync(
        string modelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current active rate card for every priced model, keyed by model id.
    /// Used by the admin model list so pricing does not require a query per model.
    /// </summary>
    Task<IReadOnlyDictionary<string, RateCardRecord>> GetActiveByModelAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the single active price for a model. Updates the existing active rate card when one
    /// exists, otherwise inserts a new one effective immediately. Prices are per million tokens.
    /// </summary>
    Task UpsertForModelAsync(
        string modelId,
        decimal inputPricePerMillionTokens,
        decimal outputPricePerMillionTokens,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every rate card for a model. No-op when the model has none.
    /// </summary>
    Task DeleteForModelAsync(
        string modelId,
        CancellationToken cancellationToken = default);
}
