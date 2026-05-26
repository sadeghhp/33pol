namespace Pol33.Core.Billing;

public sealed record RateCardRecord(
    Guid Id,
    string Slug,
    string Name,
    string ModelId,
    decimal InputPricePerMillionTokens,
    decimal OutputPricePerMillionTokens,
    string Currency,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveUntil,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
