namespace Pol33.Core.Billing;

public sealed record PlanRecord(
    Guid Id,
    string Slug,
    string Name,
    string? RateCardSlug,
    long? MonthlyTokenLimit,
    int? RequestsPerMinute,
    int? ConcurrencyLimit,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
