namespace Pol33.Persistence.Entities;

/// <summary>
/// A per-plan rate-limit tier, keyed by plan slug (matched against the tenant's plan slug). Separate
/// from the billing plans table: this holds only rate-limit config, as the appsettings
/// RateLimiting:Plans map did before the migration.
/// </summary>
public sealed class RateLimitPlanEntity
{
    public Guid Id { get; set; }

    public required string Slug { get; set; }

    public int Rpm { get; set; }

    public int Burst { get; set; }

    public int MaxConcurrentStreams { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
