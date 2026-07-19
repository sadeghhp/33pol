using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.RateLimiting;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Repositories;

public sealed class RateLimitSettingsRepository(GatewayDbContext dbContext) : IRateLimitSettingsRepository
{
    private const int DefaultsRowId = 1;
    private const int ConfigVersionRowId = 1;

    public async Task SaveAsync(
        RateLimitPolicy defaultTier,
        IReadOnlyDictionary<string, RateLimitPolicy> plans,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaultTier);
        ArgumentNullException.ThrowIfNull(plans);

        var now = DateTimeOffset.UtcNow;

        var defaults = await dbContext.RateLimitDefaults
            .FirstOrDefaultAsync(d => d.Id == DefaultsRowId, cancellationToken)
            .ConfigureAwait(false);

        if (defaults is null)
        {
            defaults = new RateLimitDefaultsEntity { Id = DefaultsRowId };
            dbContext.RateLimitDefaults.Add(defaults);
        }

        defaults.Rpm = defaultTier.Rpm;
        defaults.Burst = defaultTier.Burst;
        defaults.MaxConcurrentStreams = defaultTier.MaxConcurrentStreams;
        defaults.UpdatedAt = now;

        // Replace the plan set wholesale (small, bounded). RemoveRange keeps this provider-agnostic
        // (the EF InMemory provider used by tests does not support ExecuteDelete).
        var existingPlans = await dbContext.RateLimitPlans
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        dbContext.RateLimitPlans.RemoveRange(existingPlans);

        foreach (var (slug, tier) in plans)
        {
            dbContext.RateLimitPlans.Add(new RateLimitPlanEntity
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                Rpm = tier.Rpm,
                Burst = tier.Burst,
                MaxConcurrentStreams = tier.MaxConcurrentStreams,
                UpdatedAt = now,
            });
        }

        // Bump the config version in the same SaveChanges so the change and its version signal
        // commit atomically.
        var version = await dbContext.ConfigVersions
            .FirstOrDefaultAsync(c => c.Id == ConfigVersionRowId, cancellationToken)
            .ConfigureAwait(false);

        if (version is null)
        {
            version = new ConfigVersionEntity { Id = ConfigVersionRowId, Version = 0 };
            dbContext.ConfigVersions.Add(version);
        }

        version.Version += 1;
        version.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
